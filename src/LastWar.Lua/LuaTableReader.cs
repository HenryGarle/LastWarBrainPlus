using System.Text.Json.Nodes;

namespace LastWar.Lua;

/// <summary>
/// Evaluates a game config table. The client's tables (assets/table/*.data, a zip of Lua 5.3 chunks)
/// are code that only builds constants: NEWTABLE / LOADK / SETTABLE / SETLIST, then RETURN. This runs
/// exactly that subset - nothing is called, so nothing in a table can do anything - and turns the
/// result into rows:
///
///     return { index = { id = {1, "number"}, name = {2, "string"}, ... },
///              data  = { [1] = {1, "item_1", ...}, ... } }
///
/// Rows() pairs each data row with the column names from index.
/// </summary>
public static class LuaTableReader
{
    /// <summary>The value the chunk returns, as JSON (tables become objects keyed by their keys).</summary>
    /// <param name="lenient">For script files that define enums first and then do real work: stop at
    /// the first instruction outside the subset and return the globals defined so far.</param>
    public static JsonNode? Evaluate(LuaProto p, bool lenient = false)
    {
        var r = new object?[Math.Max(p.MaxStackSize, (byte)8) + 256];
        // Enum files (MailEnum and friends) assign globals instead of returning a table; those
        // assignments are collected and returned as one object.
        var globals = new Dictionary<object, object?>();
        object? K(int n) => p.Constants[n].Value;
        object? RK(int n) => n >= 256 ? K(n - 256) : r[n];
        for (var pc = 0; pc < p.Code.Length; pc++)
        {
            var ins = p.Code[pc];
            var op = (int)(ins & 0x3F);
            int a = (int)((ins >> 6) & 0xFF), c = (int)((ins >> 14) & 0x1FF), b = (int)((ins >> 23) & 0x1FF), bx = (int)(ins >> 14);
            switch (op)
            {
                case 0: r[a] = r[b]; break;                                        // MOVE
                case 1: r[a] = K(bx); break;                                       // LOADK
                case 2: r[a] = K((int)(p.Code[++pc] >> 6)); break;                  // LOADKX + EXTRAARG
                case 3: r[a] = b != 0; if (c != 0) pc++; break;                     // LOADBOOL
                case 4: for (var i = a; i <= a + b; i++) r[i] = null; break;       // LOADNIL
                case 10:                                                            // SETTABLE
                    if (r[a] is Dictionary<object, object?> st && RK(b) is { } sk) st[Key(sk)] = RK(c);
                    else if (!lenient) throw new InvalidDataException("SETTABLE on a non-table");
                    break;
                case 11: r[a] = new Dictionary<object, object?>(); break;          // NEWTABLE
                case 43:                                                            // SETLIST
                {
                    var t = (Dictionary<object, object?>)r[a]!;
                    var block = c == 0 ? (int)(p.Code[++pc] >> 6) : c;
                    var n = b == 0 ? 0 : b;   // b == 0 (to top) does not occur in constant tables
                    for (var i = 1; i <= n; i++) t[(long)((block - 1) * 50 + i)] = r[a + i];
                    break;
                }
                case 6: r[a] = globals.GetValueOrDefault(Key(RK(c))); break;         // GETTABUP _ENV[k]
                case 7: r[a] = (r[b] as Dictionary<object, object?>)?.GetValueOrDefault(Key(RK(c))); break;   // GETTABLE
                case 8: globals[Key(RK(b))] = RK(c); break;                         // SETTABUP _ENV[k] = v
                case 38: return b >= 2 ? ToJson(r[a]) : ToJson(globals);            // RETURN
                default:
                    // Lenient: skip what it cannot run (calls, arithmetic, closures) and carry on -
                    // the enum tables it is after are straight runs of constants.
                    if (lenient) { if (op is 36 or 12 or 44) r[a] = null; continue; }
                    throw new NotSupportedException($"table chunk uses opcode {op} at {pc} - not a plain constant table");
            }
        }
        return null;
    }

    static object Key(object? k) => k switch
    {
        double d when d == Math.Floor(d) => (long)d,
        int i => (long)i,
        null => throw new InvalidDataException("nil table key"),
        _ => k,
    };

    static JsonNode? ToJson(object? v) => v switch
    {
        null => null,
        Dictionary<object, object?> t => new JsonObject(t.Select(kv =>
            new KeyValuePair<string, JsonNode?>(Convert.ToString(kv.Key, System.Globalization.CultureInfo.InvariantCulture)!, ToJson(kv.Value)))),
        string s => JsonValue.Create(s),
        bool bl => JsonValue.Create(bl),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        _ => JsonValue.Create(v.ToString()),
    };

    /// <summary>Data rows as column-name -> value, using the table's index (1-based column numbers).</summary>
    public static List<JsonObject> Rows(JsonNode? table)
    {
        if (table?["index"] is not JsonObject index || table["data"] is not JsonObject data) return [];
        var cols = index.ToDictionary(kv => kv.Value!["1"]!.GetValue<long>().ToString(), kv => kv.Key);
        return data.Select(kv => kv.Value as JsonObject).Where(o => o is not null)
            .Select(row => new JsonObject(row!.Select(cell =>
                new KeyValuePair<string, JsonNode?>(cols.GetValueOrDefault(cell.Key, "c" + cell.Key), cell.Value?.DeepClone()))))
            .ToList();
    }
}
