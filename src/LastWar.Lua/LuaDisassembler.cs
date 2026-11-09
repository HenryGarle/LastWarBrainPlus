using System.Text;

namespace LastWar.Lua;

/// <summary>
/// Renders Lua 5.3 bytecode as a readable listing. Not a decompiler: it prints
/// instructions with constants and upvalue names resolved, which is enough to read off
/// things a constant dump cannot show - above all the ORDER of arguments to a call.
/// </summary>
public static class LuaDisassembler
{
    static readonly string[] Ops =
    [
        "MOVE","LOADK","LOADKX","LOADBOOL","LOADNIL","GETUPVAL","GETTABUP","GETTABLE",
        "SETTABUP","SETUPVAL","SETTABLE","NEWTABLE","SELF","ADD","SUB","MUL","MOD","POW",
        "DIV","IDIV","BAND","BOR","BXOR","SHL","SHR","UNM","BNOT","NOT","LEN","CONCAT",
        "JMP","EQ","LT","LE","TEST","TESTSET","CALL","TAILCALL","RETURN","FORLOOP",
        "FORPREP","TFORCALL","TFORLOOP","SETLIST","CLOSURE","VARARG","EXTRAARG",
    ];

    public static string Describe(LuaProto p, int index = 0, string path = "main")
    {
        var sb = new StringBuilder();
        sb.AppendLine($"function {path}  params={p.NumParams} vararg={p.IsVararg} " +
                      $"stack={p.MaxStackSize} lines={p.LineDefined}-{p.LastLineDefined} " +
                      $"({p.Code.Length} instructions, {p.Constants.Length} constants)");
        for (var i = 0; i < p.Code.Length; i++) sb.AppendLine("    " + Line(p, i));
        return sb.ToString();
    }

    public static string Line(LuaProto p, int i)
    {
        var ins = p.Code[i];
        var op = (int)(ins & 0x3F);
        int a = (int)((ins >> 6) & 0xFF);
        int c = (int)((ins >> 14) & 0x1FF);
        int b = (int)((ins >> 23) & 0x1FF);
        int bx = (int)(ins >> 14);
        int sbx = bx - 131071;
        var name = op < Ops.Length ? Ops[op] : $"OP{op}";

        string K(int n) => n < p.Constants.Length ? p.Constants[n].ToString() : $"k{n}";
        string RK(int n) => n >= 256 ? K(n - 256) : $"R{n}";
        string U(int n) => n < p.Upvalues.Length && p.Upvalues[n].Name.Length > 0
            ? $"U({p.Upvalues[n].Name})" : $"U{n}";

        var text = name switch
        {
            "MOVE" => $"R{a} := R{b}",
            "LOADK" => $"R{a} := {K(bx)}",
            "LOADBOOL" => $"R{a} := {(b != 0 ? "true" : "false")}",
            "LOADNIL" => $"R{a}..R{a + b} := nil",
            "GETUPVAL" => $"R{a} := {U(b)}",
            "GETTABUP" => $"R{a} := {U(b)}[{RK(c)}]",
            "GETTABLE" => $"R{a} := R{b}[{RK(c)}]",
            "SETTABUP" => $"{U(a)}[{RK(b)}] := {RK(c)}",
            "SETUPVAL" => $"{U(b)} := R{a}",
            "SETTABLE" => $"R{a}[{RK(b)}] := {RK(c)}",
            "NEWTABLE" => $"R{a} := {{}}",
            "SELF" => $"R{a + 1} := R{b}; R{a} := R{b}[{RK(c)}]",
            "CONCAT" => $"R{a} := R{b}..R{c}",
            "JMP" => $"goto {i + 1 + sbx}",
            "CALL" => $"R{a}({(b == 0 ? "..." : string.Join(", ", Enumerable.Range(a + 1, Math.Max(0, b - 1)).Select(x => $"R{x}")))})"
                      + (c == 0 ? " -> all" : c == 1 ? "" : $" -> R{a}..R{a + c - 2}"),
            "TAILCALL" => $"return R{a}(R{a + 1}..R{a + b - 1})",
            "RETURN" => b == 1 ? "return" : $"return R{a}..",
            "CLOSURE" => $"R{a} := closure #{bx}",
            "VARARG" => $"R{a}.. := ...",
            "TEST" => $"if R{a} then",
            "TESTSET" => $"if R{b} then R{a} := R{b}",
            "EQ" or "LT" or "LE" => $"if ({RK(b)} {name} {RK(c)}) != {a} then skip",
            "GETFIELD" => "",
            _ => $"A={a} B={b} C={c}",
        };
        return $"[{i,4}] {name,-9} {text}";
    }

    /// <summary>
    /// Finds prototypes whose constants mention <paramref name="needle"/>, and prints the
    /// instructions around each mention.
    /// </summary>
    /// <param name="exact">false (default) matches any constant CONTAINING the needle.</param>
    public static string FindUses(LuaProto root, string needle, int context = 14, bool exact = false)
    {
        var sb = new StringBuilder();
        var n = 0;
        foreach (var p in root.Walk())
        {
            var matches = new List<int>();
            for (var x = 0; x < p.Constants.Length; x++)
            {
                if (p.Constants[x].Kind != "string") continue;
                var v = (string?)p.Constants[x].Value ?? "";
                if (exact ? v == needle : v.Contains(needle, StringComparison.Ordinal)) matches.Add(x);
            }
            if (matches.Count == 0) continue;
            for (var i = 0; i < p.Code.Length; i++)
            {
                var ins = p.Code[i];
                var op = (int)(ins & 0x3F);
                int c = (int)((ins >> 14) & 0x1FF), b = (int)((ins >> 23) & 0x1FF);
                int bx = (int)(ins >> 14);
                var touches = (op == 1 && matches.Contains(bx))                     // LOADK
                              || (b >= 256 && matches.Contains(b - 256))             // RK(B)
                              || (c >= 256 && matches.Contains(c - 256));            // RK(C)
                if (!touches) continue;
                n++;
                sb.AppendLine($"--- use #{n} at [{i}] in a function with {p.Code.Length} instructions");
                for (var j = Math.Max(0, i - context); j < Math.Min(p.Code.Length, i + context); j++)
                    sb.AppendLine((j == i ? "  > " : "    ") + Line(p, j));
                sb.AppendLine();
            }
        }
        return n == 0 ? $"(no use of \"{needle}\" found)" : sb.ToString();
    }
}
