using ArenaDuel.Gameplay;
using ArenaDuel.Online;
using Xunit;

namespace ArenaDuel.Tests;

public sealed class NetCodecTests
{
    [Fact]
    public void Round_trips_hello_and_pick_ready()
    {
        var hello = NetCodec.Encode(MsgType.Hello, new HelloMsg { PlayerId = "p1", GameId = "arena-duel" });
        var (t, json) = NetCodec.Decode(hello);
        Assert.Equal(MsgType.Hello, t);
        var parsed = NetCodec.Parse<HelloMsg>(json);
        Assert.Equal("p1", parsed!.PlayerId);

        var pick = NetCodec.Encode(MsgType.PickReady, new PickReadyMsg
        {
            PlayerId = "p1",
            CharacterId = "akishun",
            Ready = true
        });
        var (t2, json2) = NetCodec.Decode(pick);
        Assert.Equal(MsgType.PickReady, t2);
        Assert.True(NetCodec.Parse<PickReadyMsg>(json2)!.Ready);
    }

    [Fact]
    public void Empty_payload_does_not_throw()
    {
        var (t, json) = NetCodec.Decode([]);
        Assert.Equal(0, t);
        Assert.Equal("{}", json);
    }

    [Fact]
    public void Snapshot_bytes_stay_under_udp_limit_for_typical_frame()
    {
        var snap = new MatchSnapshot
        {
            TimeLeft = 100,
            ArenaId = "arena_crimson_eaves",
            Phase = "battle",
            A = new FighterState { PlayerId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", CharacterId = "akishun", X = 40, Y = 80, Hp = 100, MaxHp = 100, Mp = 80, MaxMp = 100, Radius = 16 },
            B = new FighterState { PlayerId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", CharacterId = "qinglan", X = 400, Y = 80, Hp = 90, MaxHp = 100, Mp = 50, MaxMp = 120, Radius = 18 },
            Projectiles =
            [
                new ProjectileState { Id = 1, X = 10, Y = 10, Vx = 1, Radius = 6, IsAoePulse = false },
                new ProjectileState { Id = 2, X = 20, Y = 20, Vx = -1, Radius = 40, IsAoePulse = true }
            ]
        };
        var bytes = NetCodec.EncodeSnapshot(snap);
        Assert.True(bytes.Length <= NetCodec.MaxUnreliableBytes, $"snapshot {bytes.Length} bytes exceeds UDP {NetCodec.MaxUnreliableBytes}");
        var (type, json) = NetCodec.Decode(bytes);
        Assert.Equal(MsgType.Snapshot, type);
        var back = NetCodec.ParseSnapshot(json);
        Assert.Equal("akishun", back!.A.CharacterId);
        Assert.Equal(2, back.Projectiles.Count);
    }

    [Fact]
    public void Snapshot_with_many_projectiles_still_fits_udp_budget()
    {
        var snap = new MatchSnapshot
        {
            TimeLeft = 100,
            ArenaId = "arena_crimson_eaves",
            Phase = "battle",
            A = new FighterState { PlayerId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", CharacterId = "akishun", X = 40, Y = 80, Hp = 100, MaxHp = 100, Mp = 80, MaxMp = 100, Radius = 16 },
            B = new FighterState { PlayerId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", CharacterId = "qinglan", X = 400, Y = 80, Hp = 90, MaxHp = 100, Mp = 50, MaxMp = 120, Radius = 18 },
            Projectiles = Enumerable.Range(0, 20).Select(i => new ProjectileState
            {
                Id = i, X = i, Y = i, Vx = 1, Vy = -1, Radius = 6, IsAoePulse = i % 2 == 0
            }).ToList()
        };
        var bytes = NetCodec.EncodeSnapshot(snap);
        Assert.True(bytes.Length <= NetCodec.MaxUnreliableBytes, $"snapshot {bytes.Length} exceeds {NetCodec.MaxUnreliableBytes}");
        var back = NetCodec.ParseSnapshot(NetCodec.Decode(bytes).json);
        Assert.True(back!.Projectiles.Count <= 8);
        Assert.Equal(40, back.A.X);
    }
}
