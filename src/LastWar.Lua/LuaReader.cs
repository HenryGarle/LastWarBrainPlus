using System.Buffers.Binary;
using System.Text;

namespace LastWar.Lua;

/// <summary>
/// Reads the Lua 5.3 bytecode this game ships.
///
/// The header is NOT stock, which is why unluac and every other off-the-shelf tool
/// refuses these files: the format byte is 1 rather than 0, and only FOUR size bytes
/// follow LUAC_DATA (int, Instruction, lua_Integer, lua_Number) where stock Lua writes
/// five - the sizeof(size_t) byte is omitted. Everything after that is standard.
///
/// Verified against GameMain.luac: the first prototype's source string decodes to the
/// original build path, which only lines up if the header length is right.
/// </summary>
public sealed class LuaReader
{
    readonly byte[] _d;
    int _pos;
    int _sizeT = 8;

    LuaReader(byte[] data) => _d = data;

    public static LuaProto Load(byte[] data)
    {
        var r = new LuaReader(data);
        r.ReadHeader();
        r.ReadByte();                 // sizeupvalues of the main closure
        return r.ReadProto("");
    }

    public static LuaProto LoadFile(string path) => Load(File.ReadAllBytes(path));

    void ReadHeader()
    {
        var sig = Take(4);
        if (sig[0] != 0x1b || sig[1] != 'L' || sig[2] != 'u' || sig[3] != 'a')
            throw new InvalidDataException("not a Lua chunk");
        var version = ReadByte();
        if (version != 0x53) throw new InvalidDataException($"expected Lua 5.3 (0x53), got 0x{version:x2}");
        ReadByte();                   // format: 1 in this build, 0 in stock Lua
        Skip(6);                      // LUAC_DATA

        // Stock writes int,size_t,Instruction,lua_Integer,lua_Number. This build omits
        // size_t. Detect rather than assume: the next 8 bytes must be LUAC_INT 0x5678.
        var save = _pos;
        var five = Take(5);
        if (!LooksLikeLuacInt(_pos)) { _pos = save; var four = Take(4); _sizeT = 8; }
        else { _sizeT = five[1]; }

        Skip(8);                      // LUAC_INT
        Skip(8);                      // LUAC_NUM
    }

    bool LooksLikeLuacInt(int at) =>
        at + 8 <= _d.Length && BinaryPrimitives.ReadInt64LittleEndian(_d.AsSpan(at, 8)) == 0x5678;

    LuaProto ReadProto(string parentSource)
    {
        var source = ReadString() ?? parentSource;
        var lineDefined = ReadInt();
        var lastLine = ReadInt();
        var numParams = ReadByte();
        var isVararg = ReadByte() != 0;
        var maxStack = ReadByte();

        var code = new uint[ReadInt()];
        for (var i = 0; i < code.Length; i++) code[i] = ReadUInt();

        var constants = new LuaConstant[ReadInt()];
        for (var i = 0; i < constants.Length; i++) constants[i] = ReadConstant();

        var upvalues = new LuaUpvalue[ReadInt()];
        for (var i = 0; i < upvalues.Length; i++)
            upvalues[i] = new LuaUpvalue(ReadByte() != 0, ReadByte(), "");

        var protos = new LuaProto[ReadInt()];
        for (var i = 0; i < protos.Length; i++) protos[i] = ReadProto(source);

        // debug: line info, locals, upvalue names
        var lineInfo = new int[ReadInt()];
        for (var i = 0; i < lineInfo.Length; i++) lineInfo[i] = ReadInt();
        var nLocals = ReadInt();
        for (var i = 0; i < nLocals; i++) { ReadString(); ReadInt(); ReadInt(); }
        var nUpNames = ReadInt();
        for (var i = 0; i < nUpNames && i < upvalues.Length; i++)
            upvalues[i] = upvalues[i] with { Name = ReadString() ?? "" };
        for (var i = upvalues.Length; i < nUpNames; i++) ReadString();

        return new LuaProto
        {
            Source = source, LineDefined = lineDefined, LastLineDefined = lastLine,
            NumParams = numParams, IsVararg = isVararg, MaxStackSize = maxStack,
            Code = code, Constants = constants, Upvalues = upvalues, Protos = protos,
            LineInfo = lineInfo,
        };
    }

    LuaConstant ReadConstant()
    {
        var t = ReadByte();
        return t switch
        {
            0x00 => new LuaConstant("nil", null),
            0x01 => new LuaConstant("bool", ReadByte() != 0),
            0x03 => new LuaConstant("number", BitConverter.Int64BitsToDouble(ReadLong())),
            0x13 => new LuaConstant("int", ReadLong()),
            0x04 or 0x14 => new LuaConstant("string", ReadString() ?? ""),
            _ => throw new InvalidDataException($"unknown constant tag 0x{t:x2} at {_pos - 1}"),
        };
    }

    /// <summary>Lua stores size+1; 0xFF means a size_t length follows.</summary>
    string? ReadString()
    {
        var b = ReadByte();
        long size = b;
        if (b == 0xFF)
        {
            // The header omits sizeof(size_t), so the width of a long string's length is not stated.
            // An 8-byte read that runs past the end of the chunk means it was 4 (config tables with
            // long descriptions hit this; the scripts rarely have strings over 254 bytes).
            var wide = BinaryPrimitives.ReadInt64LittleEndian(_d.AsSpan(_pos, 8));
            size = _sizeT == 8 && wide > 0 && wide <= _d.Length - _pos ? ReadLong() : ReadInt();
        }
        if (size == 0) return null;
        return Encoding.UTF8.GetString(Take((int)size - 1));
    }

    byte ReadByte() => _d[_pos++];
    void Skip(int n) => _pos += n;
    ReadOnlySpan<byte> Take(int n) { var s = _d.AsSpan(_pos, n); _pos += n; return s; }
    int ReadInt() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    uint ReadUInt() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    long ReadLong() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
}
