using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using ArenaDuel;
using ArenaDuel.Online;
using LanHub.Sdk;
using Xunit;

namespace ArenaDuel.Tests;

/// <summary>Static self-check against README 「完全适配」 checklist.</summary>
public sealed class PlatformComplianceTests
{
    [Fact]
    public void GameId_matches_lanhub_game_manifest()
    {
        var manifestPath = FindUp("lanhub.game.json", start: AppContext.BaseDirectory);
        Assert.True(File.Exists(manifestPath), "lanhub.game.json missing next to exe / project");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var gameId = doc.RootElement.GetProperty("gameId").GetString();
        Assert.Equal(GameController.GameId, gameId);
        Assert.Equal("arena-duel", gameId);
    }

    [Fact]
    public void Project_references_LanHub_Sdk()
    {
        var csproj = FindUp("ArenaDuel.csproj", start: Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ArenaDuel")));
        if (!File.Exists(csproj))
            csproj = FindUp("ArenaDuel.csproj", start: AppContext.BaseDirectory);
        Assert.True(File.Exists(csproj), "ArenaDuel.csproj not found");
        var xml = XDocument.Load(csproj);
        var refs = xml.Descendants("ProjectReference").Select(e => (string?)e.Attribute("Include")).ToList();
        Assert.Contains(refs, r => r != null && r.Contains("LanHub.Sdk", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sdk_and_Core_assemblies_load_with_game()
    {
        Assert.NotNull(typeof(LanHubClient).Assembly);
        Assert.NotNull(typeof(LanHub.Core.Protocol.FrameCodec).Assembly);
        Assert.Contains("LanHub.Sdk", typeof(LanHubClient).Assembly.GetName().Name);
    }

    [Fact]
    public void Connect_path_is_environment_variable_based()
    {
        // Equiv to ConnectFromEnvironmentAsync — no hardcoded remote IP / discovery.
        var src = ReadGameSource("Online/HubConnect.cs");
        Assert.Contains("LANHUB_TOKEN", src);
        Assert.Contains("LANHUB_GAME_ID", src);
        Assert.Contains("LANHUB_PLAYER_ID", src);
        Assert.Contains("LANHUB_SDK_PORT", src);
        Assert.Contains("ConnectAsync", src);
        Assert.DoesNotContain("UdpClient", src);
        Assert.DoesNotContain("Beacon", src);
    }

    [Fact]
    public void Subscribes_required_sdk_events()
    {
        var src = ReadGameSource("GameController.cs");
        Assert.Contains("MessageReceived", src);
        Assert.Contains("RosterUpdated", src);
        Assert.Contains("StateChanged", src);
        Assert.Contains("Disconnected", src);
        Assert.Contains("SessionUpdated", src);
        Assert.Contains("PlayerJoined", src);
        Assert.Contains("PlayerLeft", src);
        Assert.Contains("Connected", src);
    }

    [Fact]
    public void Host_authority_and_channel_split()
    {
        var src = ReadGameSource("GameController.cs");
        Assert.Contains("IsHost", src);
        Assert.Contains("SendUnreliableToAllAsync", src);
        Assert.Contains("SendToAllAsync", src);
        Assert.Contains("LanHubChannel.Reliable", src);
        Assert.Contains("EncodeSnapshot", src);
        Assert.Contains("EncodeInput", src);
        Assert.Contains("MsgType.MatchStart", src);
        Assert.Contains("MsgType.MatchEnd", src);
        Assert.Contains("MsgType.PickReady", src);
        Assert.Contains("Reconnecting", src);
        Assert.Contains("DisposeAsync", src);
    }

    [Fact]
    public void No_parallel_lan_discovery_lobby()
    {
        var src = ReadGameSource("GameController.cs") + ReadGameSource("Online/HubConnect.cs");
        Assert.DoesNotContain("37810", src); // discovery UDP
        Assert.DoesNotContain("UdpBeacon", src);
        Assert.DoesNotContain("IPAddress.Broadcast", src);
    }

    [Fact]
    public void Output_dir_ships_manifest_and_sdk_deps()
    {
        var baseDir = AppContext.BaseDirectory;
        Assert.True(File.Exists(Path.Combine(baseDir, "lanhub.game.json"))
                    || File.Exists(FindUp("lanhub.game.json", baseDir)),
            "lanhub.game.json should copy to output");
        Assert.True(File.Exists(Path.Combine(baseDir, "LanHub.Sdk.dll")));
        Assert.True(File.Exists(Path.Combine(baseDir, "LanHub.Core.dll")));
    }

    [Fact]
    public void Wire_snapshot_fits_unreliable_budget()
    {
        Assert.Equal(1200, NetCodec.MaxUnreliableBytes);
        var snap = new ArenaDuel.Gameplay.MatchSnapshot
        {
            TimeLeft = 90,
            ArenaId = "arena_obsidian_ring",
            Phase = "battle",
            A = new ArenaDuel.Gameplay.FighterState
            {
                PlayerId = new string('a', 32),
                CharacterId = "akishun",
                X = 1, Y = 2, Hp = 100, MaxHp = 100, Mp = 50, MaxMp = 80, Radius = 16
            },
            B = new ArenaDuel.Gameplay.FighterState
            {
                PlayerId = new string('b', 32),
                CharacterId = "qinglan",
                X = 3, Y = 4, Hp = 90, MaxHp = 100, Mp = 40, MaxMp = 90, Radius = 18
            },
            Projectiles = Enumerable.Range(0, 12).Select(i => new ArenaDuel.Gameplay.ProjectileState
            {
                Id = i, X = i, Y = i, Vx = 1, Vy = 1, Radius = 6, IsAoePulse = i % 2 == 0
            }).ToList()
        };
        var bytes = NetCodec.EncodeSnapshot(snap);
        Assert.True(bytes.Length <= NetCodec.MaxUnreliableBytes, $"got {bytes.Length}");
    }

    private static string ReadGameSource(string relative)
    {
        var root = FindUp("ArenaDuel.csproj", start: Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ArenaDuel")));
        if (!File.Exists(root))
            root = FindUp("ArenaDuel.csproj", AppContext.BaseDirectory);
        var dir = Path.GetDirectoryName(root)!;
        var path = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), path);
        return File.ReadAllText(path);
    }

    private static string FindUp(string fileName, string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
            // also search one child named ArenaDuel
            var child = Path.Combine(dir.FullName, "ArenaDuel", fileName);
            if (File.Exists(child)) return child;
            dir = dir.Parent;
        }
        return Path.Combine(start, fileName);
    }
}
