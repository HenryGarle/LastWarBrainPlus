using System.Buffers.Binary;
using System.Text;

namespace LastWar.Protocol.Sfs;

/// <summary>
/// SFS2X value serialisation. Everything is big-endian; a UtfString is an int16 byte
/// count followed by UTF-8; a Text is the same with an int32 count.
/// </summary>
public static class SfsCodec
{
    public static byte[] Encode(SfsObject o)
    {
        var buf = new MemoryStream();
        WriteObject(buf, o);
        return buf.ToArray();
    }

    public static SfsObject Decode(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        var type = (SfsType)Read1(data, ref pos);
        if (type != SfsType.SfsObject)
            throw new InvalidDataException($"expected SfsObject at offset 0, got {type}");
        return ReadObjectBody(data, ref pos);
    }

    // ---- writing ----

    static void WriteObject(Stream s, SfsObject o)
    {
        s.WriteByte((byte)SfsType.SfsObject);
        WriteInt16(s, checked((short)o.Count));
        foreach (var k in o.Keys)
        {
            WriteUtf(s, k);
            o.TryGet(k, out var v);
            WriteTagged(s, v);
        }
    }

    static void WriteArray(Stream s, SfsArray a)
    {
        s.WriteByte((byte)SfsType.SfsArray);
        WriteInt16(s, checked((short)a.Count));
        foreach (var v in a.Items) WriteTagged(s, v);
    }

    static void WriteTagged(Stream s, SfsValue v)
    {
        if (v.Type is SfsType.SfsObject) { WriteObject(s, (SfsObject)v.Value!); return; }
        if (v.Type is SfsType.SfsArray) { WriteArray(s, (SfsArray)v.Value!); return; }
        s.WriteByte((byte)v.Type);
        WritePayload(s, v);
    }

    static void WritePayload(Stream s, SfsValue v)
    {
        switch (v.Type)
        {
            case SfsType.Null: break;
            case SfsType.Bool: s.WriteByte((bool)v.Value! ? (byte)1 : (byte)0); break;
            case SfsType.Byte: s.WriteByte((byte)v.Value!); break;
            case SfsType.Short: WriteInt16(s, (short)v.Value!); break;
            case SfsType.Int: WriteInt32(s, (int)v.Value!); break;
            case SfsType.Long: WriteInt64(s, (long)v.Value!); break;
            case SfsType.Float: WriteInt32(s, BitConverter.SingleToInt32Bits((float)v.Value!)); break;
            case SfsType.Double: WriteInt64(s, BitConverter.DoubleToInt64Bits((double)v.Value!)); break;
            case SfsType.UtfString: WriteUtf(s, (string)v.Value!); break;
            case SfsType.Text:
            {
                var b = Encoding.UTF8.GetBytes((string)v.Value!);
                WriteInt32(s, b.Length); s.Write(b);
                break;
            }
            case SfsType.ByteArray:
            {
                var b = (byte[])v.Value!;
                WriteInt32(s, b.Length); s.Write(b);
                break;
            }
            case SfsType.IntArray:
            {
                var a = (int[])v.Value!;
                WriteInt16(s, checked((short)a.Length));
                foreach (var i in a) WriteInt32(s, i);
                break;
            }
            case SfsType.LongArray:
            {
                var a = (long[])v.Value!;
                WriteInt16(s, checked((short)a.Length));
                foreach (var i in a) WriteInt64(s, i);
                break;
            }
            case SfsType.UtfStringArray:
            {
                var a = (string[])v.Value!;
                WriteInt16(s, checked((short)a.Length));
                foreach (var i in a) WriteUtf(s, i);
                break;
            }
            default:
                throw new NotSupportedException($"cannot encode SFS type {v.Type}");
        }
    }

    static void WriteUtf(Stream s, string v)
    {
        var b = Encoding.UTF8.GetBytes(v);
        WriteInt16(s, checked((short)b.Length));
        s.Write(b);
    }

    static void WriteInt16(Stream s, short v)
    { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, v); s.Write(b); }

    static void WriteInt32(Stream s, int v)
    { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); s.Write(b); }

    static void WriteInt64(Stream s, long v)
    { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); s.Write(b); }

    // ---- reading ----

    static SfsObject ReadObjectBody(ReadOnlySpan<byte> d, ref int pos)
    {
        var o = new SfsObject();
        var count = ReadInt16(d, ref pos);
        for (var i = 0; i < count; i++)
        {
            var key = ReadUtf(d, ref pos);
            o.Put(key, ReadTagged(d, ref pos));
        }
        return o;
    }

    static SfsArray ReadArrayBody(ReadOnlySpan<byte> d, ref int pos)
    {
        var a = new SfsArray();
        var count = ReadInt16(d, ref pos);
        for (var i = 0; i < count; i++) a.Add(ReadTagged(d, ref pos));
        return a;
    }

    static SfsValue ReadTagged(ReadOnlySpan<byte> d, ref int pos)
    {
        var t = (SfsType)Read1(d, ref pos);
        switch (t)
        {
            case SfsType.Null: return new SfsValue(t, null);
            case SfsType.Bool: return new SfsValue(t, Read1(d, ref pos) != 0);
            case SfsType.Byte: return new SfsValue(t, Read1(d, ref pos));
            case SfsType.Short: return new SfsValue(t, ReadInt16(d, ref pos));
            case SfsType.Int: return new SfsValue(t, ReadInt32(d, ref pos));
            case SfsType.Long: return new SfsValue(t, ReadInt64(d, ref pos));
            case SfsType.Float: return new SfsValue(t, BitConverter.Int32BitsToSingle(ReadInt32(d, ref pos)));
            case SfsType.Double: return new SfsValue(t, BitConverter.Int64BitsToDouble(ReadInt64(d, ref pos)));
            case SfsType.UtfString: return new SfsValue(t, ReadUtf(d, ref pos));
            case SfsType.Text:
            {
                var n = ReadInt32(d, ref pos);
                var s = Encoding.UTF8.GetString(Slice(d, ref pos, n));
                return new SfsValue(t, s);
            }
            case SfsType.ByteArray:
            {
                var n = ReadInt32(d, ref pos);
                return new SfsValue(t, Slice(d, ref pos, n).ToArray());
            }
            case SfsType.BoolArray:
            {
                var n = ReadInt16(d, ref pos);
                var a = new bool[n];
                for (var i = 0; i < n; i++) a[i] = Read1(d, ref pos) != 0;
                return new SfsValue(t, a);
            }
            case SfsType.ShortArray:
            {
                var n = ReadInt16(d, ref pos);
                var a = new short[n];
                for (var i = 0; i < n; i++) a[i] = ReadInt16(d, ref pos);
                return new SfsValue(t, a);
            }
            case SfsType.IntArray:
            {
                var n = ReadInt16(d, ref pos);
                var a = new int[n];
                for (var i = 0; i < n; i++) a[i] = ReadInt32(d, ref pos);
                return new SfsValue(t, a);
            }
            case SfsType.LongArray:
            {
                var n = ReadInt16(d, ref pos);
                var a = new long[n];
                for (var i = 0; i < n; i++) a[i] = ReadInt64(d, ref pos);
                return new SfsValue(t, a);
            }
            case SfsType.FloatArray:
            {
                var n = ReadInt16(d, ref pos);
                var a = new float[n];
                for (var i = 0; i < n; i++) a[i] = BitConverter.Int32BitsToSingle(ReadInt32(d, ref pos));
                return new SfsValue(t, a);
            }
            case SfsType.DoubleArray:
            {
                var n = ReadInt16(d, ref pos);
                var a = new double[n];
                for (var i = 0; i < n; i++) a[i] = BitConverter.Int64BitsToDouble(ReadInt64(d, ref pos));
                return new SfsValue(t, a);
            }
            case SfsType.UtfStringArray:
            {
                var n = ReadInt16(d, ref pos);
                var a = new string[n];
                for (var i = 0; i < n; i++) a[i] = ReadUtf(d, ref pos);
                return new SfsValue(t, a);
            }
            case SfsType.SfsArray: return new SfsValue(t, ReadArrayBody(d, ref pos));
            case SfsType.SfsObject: return new SfsValue(t, ReadObjectBody(d, ref pos));
            default: throw new InvalidDataException($"unknown SFS type {(byte)t} at offset {pos - 1}");
        }
    }

    static string ReadUtf(ReadOnlySpan<byte> d, ref int pos)
    {
        var n = ReadInt16(d, ref pos);
        return Encoding.UTF8.GetString(Slice(d, ref pos, n));
    }

    static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> d, ref int pos, int n)
    {
        if (n < 0 || pos + n > d.Length)
            throw new InvalidDataException($"truncated payload: want {n} bytes at {pos}, have {d.Length - pos}");
        var s = d.Slice(pos, n);
        pos += n;
        return s;
    }

    static byte Read1(ReadOnlySpan<byte> d, ref int pos) => Slice(d, ref pos, 1)[0];
    static short ReadInt16(ReadOnlySpan<byte> d, ref int pos) => BinaryPrimitives.ReadInt16BigEndian(Slice(d, ref pos, 2));
    static int ReadInt32(ReadOnlySpan<byte> d, ref int pos) => BinaryPrimitives.ReadInt32BigEndian(Slice(d, ref pos, 4));
    static long ReadInt64(ReadOnlySpan<byte> d, ref int pos) => BinaryPrimitives.ReadInt64BigEndian(Slice(d, ref pos, 8));
}
