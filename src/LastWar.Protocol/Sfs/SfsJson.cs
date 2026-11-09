using System.Text.Json;
using System.Text.Json.Nodes;
namespace LastWar.Protocol.Sfs;

/// <summary>
/// Turns a JSON object into SFS parameters, so commands can be issued from the command
/// line the way they are documented in AGENTS.md. Unlike the Go client's interactive
/// mode this handles arrays, which world.get.block's "index" parameter requires.
/// </summary>
public static class SfsJson
{
    public static SfsObject Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("parameters must be a JSON object");
        return ToObject(doc.RootElement);
    }

    static SfsObject ToObject(JsonElement el)
    {
        var o = new SfsObject();
        foreach (var prop in el.EnumerateObject()) o.Put(prop.Name, ToValue(prop.Value));
        return o;
    }

    static SfsValue ToValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => SfsValue.Of(el.GetString()!),
        JsonValueKind.True => SfsValue.Of(true),
        JsonValueKind.False => SfsValue.Of(false),
        JsonValueKind.Null => new SfsValue(SfsType.Null, null),
        JsonValueKind.Number => el.TryGetInt32(out var i) ? SfsValue.Of(i) : SfsValue.Of(el.GetInt64()),
        JsonValueKind.Object => new SfsValue(SfsType.SfsObject, ToObject(el)),
        JsonValueKind.Array => ToArray(el),
        _ => throw new ArgumentException($"unsupported JSON value {el.ValueKind}"),
    };

    /// <summary>All-integer arrays become an SFS int array; anything else becomes an SFS array.</summary>
    static SfsValue ToArray(JsonElement el)
    {
        var items = el.EnumerateArray().ToList();
        if (items.Count > 0 && items.All(i => i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out _)))
            return SfsValue.Of(items.Select(i => i.GetInt32()).ToArray());
        var arr = new SfsArray();
        foreach (var i in items) arr.Add(ToValue(i));
        return new SfsValue(SfsType.SfsArray, arr);
    }

    // ---- the other direction: SFS -> JSON, so replies and frames can be shown in a UI

    public static JsonNode Write(SfsObject o)
    {
        var n = new JsonObject();
        foreach (var key in o.Keys) if (o.TryGet(key, out var v)) n[key] = Write(v);
        return n;
    }

    public static JsonNode Write(SfsArray a)
    {
        var n = new JsonArray();
        foreach (var v in a.Items) n.Add(Write(v));
        return n;
    }

    static JsonNode? Write(SfsValue v) => v.Type switch
    {
        SfsType.Null => null,
        SfsType.SfsObject => Write((SfsObject)v.Value!),
        SfsType.SfsArray => Write((SfsArray)v.Value!),
        SfsType.ByteArray => JsonValue.Create(Convert.ToHexString((byte[])v.Value!)),
        SfsType.BoolArray or SfsType.ShortArray or SfsType.IntArray
            or SfsType.LongArray or SfsType.FloatArray or SfsType.DoubleArray
            or SfsType.UtfStringArray => Enumerable(v.Value),
        _ => JsonValue.Create(v.Value),
    };

    /// <summary>The typed SFS arrays all arrive as plain CLR arrays.</summary>
    static JsonNode Enumerable(object? value)
    {
        var n = new JsonArray();
        if (value is System.Collections.IEnumerable e)
            foreach (var item in e) n.Add(JsonValue.Create(item));
        return n;
    }
}