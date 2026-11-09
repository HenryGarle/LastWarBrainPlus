using LastWar.Client.Game;

namespace LastWar.Tests;

public class ProtoTests
{
    // The first stronghold of 1967's world.get.city.stronghold.info reply (2026-09-27):
    // id 1, tag SP4L, an alliance id, level 67, name Spartaner, server 1967 (varint af 0f).
    [Fact]
    public void ReadsTheStrongholdList()
    {
        var bytes = Convert.FromBase64String("CjoIARIEU1A0TBogMTQ5NDVhZDIzYmNiNDljMWFiNmYzZGUyZjQwYjE2ODIgQzIJU3BhcnRhbmVyOK8P");
        var outer = Assert.Single(Proto.Fields(bytes));
        Assert.Equal(1, outer.No);
        var f = Proto.Fields(outer.Bytes!).ToDictionary(x => x.No);
        Assert.Equal((1L, 67L, 1967L), (f[1].Value, f[4].Value, f[7].Value));
        Assert.Equal("SP4L", System.Text.Encoding.UTF8.GetString(f[2].Bytes!));
        Assert.Equal("Spartaner", System.Text.Encoding.UTF8.GetString(f[6].Bytes!));
        Assert.Equal(32, f[3].Bytes!.Length);
    }
}
