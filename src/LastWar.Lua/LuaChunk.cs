namespace LastWar.Lua;

public sealed record LuaConstant(string Kind, object? Value)
{
    public override string ToString() => Kind switch
    {
        "string" => "\"" + Value + "\"",
        "nil" => "nil",
        _ => Value?.ToString() ?? "?",
    };
}

public sealed record LuaUpvalue(bool InStack, byte Index, string Name);

public sealed class LuaProto
{
    public string Source { get; init; } = "";
    public int LineDefined { get; init; }
    public int LastLineDefined { get; init; }
    public byte NumParams { get; init; }
    public bool IsVararg { get; init; }
    public byte MaxStackSize { get; init; }
    public uint[] Code { get; init; } = [];
    public LuaConstant[] Constants { get; init; } = [];
    public LuaUpvalue[] Upvalues { get; init; } = [];
    public LuaProto[] Protos { get; init; } = [];
    public int[] LineInfo { get; init; } = [];

    /// <summary>Every prototype in this tree, outermost first.</summary>
    public IEnumerable<LuaProto> Walk()
    {
        yield return this;
        foreach (var p in Protos)
            foreach (var q in p.Walk())
                yield return q;
    }
}
