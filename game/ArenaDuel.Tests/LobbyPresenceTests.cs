using ArenaDuel.Online;
using Xunit;

namespace ArenaDuel.Tests;

public sealed class LobbyPresenceTests
{
    [Fact]
    public void Does_not_enter_pick_until_two_game_processes_hello()
    {
        var p = new LobbyPresence();
        p.Mark("host");
        Assert.False(LobbyPresence.ShouldEnterPick(roomCount: 2, presentCount: p.Count));
        p.Mark("guest");
        Assert.True(LobbyPresence.ShouldEnterPick(roomCount: 2, presentCount: p.Count));
    }

    [Fact]
    public void Room_of_two_is_not_enough_without_presence()
    {
        Assert.False(LobbyPresence.ShouldEnterPick(roomCount: 2, presentCount: 1));
        Assert.False(LobbyPresence.ShouldEnterPick(roomCount: 1, presentCount: 2));
    }

    [Fact]
    public void ExpireStale_drops_silent_peers_but_keeps_self()
    {
        var p = new LobbyPresence(TimeSpan.FromSeconds(2));
        var t0 = DateTime.UtcNow;
        p.Mark("self", t0);
        p.Mark("peer", t0.AddSeconds(-5));
        Assert.Equal(1, p.ExpireStale(t0, keepPlayerId: "self"));
        Assert.True(p.Contains("self"));
        Assert.False(p.Contains("peer"));
    }

    [Fact]
    public void Remove_and_clear_drop_presence()
    {
        var p = new LobbyPresence();
        p.Mark("a");
        p.Mark("b");
        p.Remove("b");
        Assert.Equal(1, p.Count);
        p.Clear();
        Assert.Equal(0, p.Count);
    }
}
