using LanHub.Core.Models;
using Xunit;

namespace LanHub.Core.Tests;

public sealed class GameIdMatcherTests
{
    [Theory]
    [InlineData("arena-duel", "arena-duel", true)]
    [InlineData("arena-duel", "Arena-Duel", true)]
    [InlineData("arena-duel", "arenaduel", true)]
    [InlineData("lan-chat", "lanchat", true)]
    [InlineData("arena-duel", "lan-chat", false)]
    [InlineData("", "arena-duel", false)]
    public void EqualsLoose_matches_normalized_forms(string a, string b, bool expected)
    {
        Assert.Equal(expected, GameIdMatcher.EqualsLoose(a, b));
    }

    [Fact]
    public void FindInLibrary_prefers_exact_GameId()
    {
        var games = new[]
        {
            new GameEntry { GameId = "lan-chat", Title = "Chat", ExePath = @"C:\x\LanChat.exe" },
            new GameEntry { GameId = "arena-duel", Title = "Arena", ExePath = @"C:\x\ArenaDuel.exe" }
        };

        var found = GameIdMatcher.FindInLibrary(games, "arena-duel");
        Assert.NotNull(found);
        Assert.Equal("arena-duel", found!.GameId);
    }

    [Fact]
    public void FindInLibrary_matches_filename_when_GameId_drifted()
    {
        var games = new[]
        {
            new GameEntry { GameId = "arenaduel", Title = "Arena", ExePath = @"C:\games\ArenaDuel.exe" }
        };

        var found = GameIdMatcher.FindInLibrary(games, "arena-duel");
        Assert.NotNull(found);
        Assert.Equal("arenaduel", found!.GameId);
    }

    [Fact]
    public void FindInLibrary_does_not_pick_wrong_single_game_for_real_id()
    {
        var games = new[]
        {
            new GameEntry { GameId = "other-game", Title = "Other", ExePath = @"C:\x\Other.exe" }
        };

        Assert.Null(GameIdMatcher.FindInLibrary(games, "arena-duel"));
    }

    [Fact]
    public void TryHealGameId_rewrites_drifted_id()
    {
        var entry = new GameEntry { GameId = "arenaduel", ExePath = @"C:\games\ArenaDuel.exe" };
        Assert.True(GameIdMatcher.TryHealGameId(entry, "arena-duel"));
        Assert.Equal("arena-duel", entry.GameId);
    }

    [Theory]
    [InlineData("ca9c8a7f", true)]
    [InlineData("arena-duel", false)]
    [InlineData("arenaduel", false)]
    [InlineData("", true)]
    public void IsLikelyEphemeralGameId_detects_legacy_random(string id, bool expected)
    {
        Assert.Equal(expected, GameIdMatcher.IsLikelyEphemeralGameId(id));
    }

    [Fact]
    public void EnsureCanonicalFromManifest_rewrites_ephemeral_from_sidecar()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lanhub-gid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "ArenaDuel.exe");
            File.WriteAllBytes(exe, [0]);
            File.WriteAllText(Path.Combine(dir, "lanhub.game.json"),
                """{"gameId":"arena-duel","title":"Arena"}""");

            var entry = new GameEntry { GameId = "ca9c8a7f", ExePath = exe, Title = "x" };
            Assert.True(GameIdMatcher.EnsureCanonicalFromManifest(entry));
            Assert.Equal("arena-duel", entry.GameId);
            Assert.Equal("Arena", entry.Title);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void TryHealGameId_does_not_overwrite_with_ephemeral()
    {
        var entry = new GameEntry { GameId = "arena-duel", ExePath = @"C:\games\ArenaDuel.exe" };
        Assert.False(GameIdMatcher.TryHealGameId(entry, "ca9c8a7f"));
        Assert.Equal("arena-duel", entry.GameId);
    }
}
