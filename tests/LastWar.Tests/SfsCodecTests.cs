using LastWar.Protocol.Sfs;

namespace LastWar.Tests;

public class SfsCodecTests
{
    /// <summary>
    /// Golden vector computed by hand from the SFS2X format, NOT from this encoder, so a
    /// mistake in the writer cannot make the test agree with itself:
    ///   12       SfsObject
    ///   00 01    one entry
    ///   00 01 61 key "a" (int16 length + UTF-8)
    ///   04       Int
    ///   00 00 00 2A  value 42, big-endian
    /// </summary>
    [Fact]
    public void EncodesTheDocumentedByteLayout()
    {
        var o = new SfsObject();
        o.PutInt("a", 42);

        Assert.Equal("120001000161040000002A", Convert.ToHexString(SfsCodec.Encode(o)));
    }

    [Fact]
    public void DecodesTheGoldenVector()
    {
        var decoded = SfsCodec.Decode(Convert.FromHexString("120001000161040000002A"));

        Assert.Equal(42, decoded.GetInt("a"));
    }

    [Fact]
    public void RoundTripsEveryScalarWeSend()
    {
        var o = new SfsObject();
        o.PutInt("i", -7);
        o.PutLong("l", 9_007_199_254_740_993L);
        o.PutBool("b", true);
        o.PutDouble("d", 1.5);
        o.PutUtfString("s", "Käsiger Elmo \u30cf\u30ca111");   // non-ASCII names are the norm
        o.PutIntArray("index", [0, 1, 159, 62_499]);

        var back = SfsCodec.Decode(SfsCodec.Encode(o));

        Assert.Equal(-7, back.GetInt("i"));
        Assert.Equal(9_007_199_254_740_993L, back.GetLong("l"));
        Assert.Equal("Käsiger Elmo \u30cf\u30ca111", back.GetString("s"));
        Assert.True(back.TryGet("b", out var b) && (bool)b.Value!);
        Assert.True(back.TryGet("d", out var d) && (double)d.Value! == 1.5);
        Assert.Equal([0, 1, 159, 62_499], (int[])back.Get("index"));
    }

    [Fact]
    public void RoundTripsNestedObjectsAndArrays()
    {
        var inner = new SfsObject();
        inner.PutUtfString("uid", "1240780578001963");
        var arr = new SfsArray();
        arr.AddObject(inner);
        arr.Add(new SfsValue(SfsType.ByteArray, new byte[] { 8, 130, 47, 16, 6 }));
        var root = new SfsObject();
        root.PutArray("serverPointArr", arr);

        var back = SfsCodec.Decode(SfsCodec.Encode(root));

        Assert.True(back.TryGetArray("serverPointArr", out var got));
        Assert.Equal("1240780578001963", got.Objects().Single().GetString("uid"));
        Assert.Equal([8, 130, 47, 16, 6], got.ByteArrays().Single());
    }

    [Fact]
    public void FrameRoundTripsSmallBodyUncompressed()
    {
        var body = SfsCodec.Encode(Build("cmd", "world.get.block"));

        var frame = SfsPacket.Encode(body);
        var back = Read(frame);

        Assert.Equal(body, back);
        Assert.Equal(0, frame[0] & (byte)SfsPacket.Header.Compressed);
    }

    [Fact]
    public void FrameCompressesAboveThreshold()
    {
        var o = new SfsObject();
        for (var i = 0; i < 400; i++) o.PutUtfString($"k{i}", "padding-padding-padding");
        var body = SfsCodec.Encode(o);
        Assert.True(body.Length > SfsPacket.CompressionThreshold);

        var frame = SfsPacket.Encode(body);

        Assert.NotEqual(0, frame[0] & (byte)SfsPacket.Header.Compressed);
        Assert.Equal(body, Read(frame));
    }

    /// <summary>The XOR key is the payload length, so a body whose length changes must still round-trip.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(1023)]
    public void XorSurvivesAwkwardLengths(int pad)
    {
        var o = new SfsObject();
        o.PutUtfString("p", new string('x', pad));
        var body = SfsCodec.Encode(o);

        Assert.Equal(body, Read(SfsPacket.Encode(body)));
    }

    static SfsObject Build(string key, string value)
    {
        var o = new SfsObject();
        o.PutUtfString(key, value);
        return o;
    }

    static byte[] Read(byte[] frame)
    {
        using var ms = new MemoryStream(frame);
        return SfsPacket.ReadAsync(ms).GetAwaiter().GetResult();
    }
}

file static class Ext
{
    public static int[] Get(this SfsObject o, string key) =>
        o.TryGet(key, out var v) ? (int[])v.Value! : [];
}
