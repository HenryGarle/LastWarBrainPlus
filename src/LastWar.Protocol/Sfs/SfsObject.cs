using System.Diagnostics.CodeAnalysis;

namespace LastWar.Protocol.Sfs;

/// <summary>One tagged value. <see cref="Value"/> holds the CLR type matching <see cref="Type"/>.</summary>
public readonly record struct SfsValue(SfsType Type, object? Value)
{
    public static SfsValue Of(bool v) => new(SfsType.Bool, v);
    public static SfsValue Of(int v) => new(SfsType.Int, v);
    public static SfsValue Of(long v) => new(SfsType.Long, v);
    public static SfsValue Of(double v) => new(SfsType.Double, v);
    public static SfsValue Of(string v) => new(SfsType.UtfString, v);
    public static SfsValue Of(int[] v) => new(SfsType.IntArray, v);
    public static SfsValue Of(byte[] v) => new(SfsType.ByteArray, v);
}

/// <summary>
/// An ordered key/value map, the unit of every request and response. Insertion order is
/// preserved because the server is order-sensitive on some commands.
/// </summary>
public sealed class SfsObject
{
    readonly List<string> _keys = [];
    readonly Dictionary<string, SfsValue> _values = [];

    public IReadOnlyList<string> Keys => _keys;
    public int Count => _keys.Count;

    public void Put(string key, SfsValue value)
    {
        if (!_values.ContainsKey(key)) _keys.Add(key);
        _values[key] = value;
    }

    public void PutInt(string key, int v) => Put(key, SfsValue.Of(v));
    public void PutLong(string key, long v) => Put(key, SfsValue.Of(v));
    public void PutBool(string key, bool v) => Put(key, SfsValue.Of(v));
    public void PutDouble(string key, double v) => Put(key, SfsValue.Of(v));
    public void PutUtfString(string key, string v) => Put(key, SfsValue.Of(v));
    /// <summary>Required by world.get.block's "index" parameter.</summary>
    public void PutIntArray(string key, int[] v) => Put(key, SfsValue.Of(v));
    public void PutObject(string key, SfsObject v) => Put(key, new SfsValue(SfsType.SfsObject, v));
    public void PutArray(string key, SfsArray v) => Put(key, new SfsValue(SfsType.SfsArray, v));

    public bool TryGet(string key, out SfsValue value) => _values.TryGetValue(key, out value);
    public bool Has(string key) => _values.ContainsKey(key);

    public string GetString(string key) =>
        _values.TryGetValue(key, out var v) && v.Value is string s ? s : "";

    public int GetInt(string key) => _values.TryGetValue(key, out var v) ? v.Value switch
    {
        int i => i, long l => (int)l, short sh => sh, byte b => b, _ => 0,
    } : 0;

    public long GetLong(string key) => _values.TryGetValue(key, out var v) ? v.Value switch
    {
        long l => l, int i => i, short sh => sh, byte b => b, _ => 0L,
    } : 0L;

    public bool TryGetObject(string key, [NotNullWhen(true)] out SfsObject? obj)
    {
        obj = _values.TryGetValue(key, out var v) ? v.Value as SfsObject : null;
        return obj is not null;
    }

    public bool TryGetArray(string key, [NotNullWhen(true)] out SfsArray? arr)
    {
        arr = _values.TryGetValue(key, out var v) ? v.Value as SfsArray : null;
        return arr is not null;
    }

    /// <summary>Scalar rendered as text, without the caller caring which numeric width the server chose.</summary>
    public string Scalar(string key) => _values.TryGetValue(key, out var v) ? v.Value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        byte[] b2 => $"<{b2.Length} bytes>",
        string[] sa => "[" + string.Join(", ", sa) + "]",
        int[] ia => "[" + string.Join(", ", ia) + "]",
        long[] la => "[" + string.Join(", ", la) + "]",
        SfsObject => "<object>",
        SfsArray => "<array>",
        var o => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    } : "";

    public override string ToString() =>
        "{" + string.Join(", ", _keys.Select(k => $"{k}={Scalar(k)}")) + "}";
}

/// <summary>An ordered list of tagged values.</summary>
public sealed class SfsArray
{
    readonly List<SfsValue> _items = [];
    public IReadOnlyList<SfsValue> Items => _items;
    public int Count => _items.Count;
    public void Add(SfsValue v) => _items.Add(v);
    public void AddObject(SfsObject v) => _items.Add(new SfsValue(SfsType.SfsObject, v));

    /// <summary>Byte-array items, which is how the game ships protobuf payloads.</summary>
    public IEnumerable<byte[]> ByteArrays() => _items.Select(i => i.Value).OfType<byte[]>();
    public IEnumerable<SfsObject> Objects() => _items.Select(i => i.Value).OfType<SfsObject>();
}
