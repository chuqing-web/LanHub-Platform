using System.IO;
using System.Text.Json;
using LanChat;
using LanChat.Online;
using LanHub.Core.Models;
using Xunit;

namespace LanChat.Tests;

public class PlatformAdaptTests
{
    [Fact]
    public void Manifest_Matches_ConstGameId()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "lanhub.game.json");
        // Prefer source-of-truth beside project output of LanChat
        var candidates = new[]
        {
            path,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LanChat", "lanhub.game.json")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LanChat", "bin", "Debug", "net8.0-windows", "lanhub.game.json")),
        };

        string? found = candidates.FirstOrDefault(File.Exists);
        Assert.True(found != null, "找不到 lanhub.game.json");

        var raw = File.ReadAllText(found!);
        using var doc = JsonDocument.Parse(raw);
        var gameId = doc.RootElement.GetProperty("gameId").GetString();
        Assert.Equal(ChatSession.GameId, gameId);

        var tmp = Path.Combine(Path.GetTempPath(), "lanchat-adapt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.Copy(found!, Path.Combine(tmp, "lanhub.game.json"));
            var fakeExe = Path.Combine(tmp, "LanChat.exe");
            File.WriteAllBytes(fakeExe, [0]);
            var m = GameManifest.TryLoadBesideExe(fakeExe);
            Assert.NotNull(m);
            Assert.Equal(ChatSession.GameId, m!.GameId);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ChatCodec_RoundTrip_ReliableChat()
    {
        var wire = new ChatWire
        {
            FromId = "p1",
            FromName = "Alice",
            Text = "你好 LanHub",
            ToId = null,
            UtcUnixMs = 1_700_000_000_000
        };
        var bytes = ChatCodec.EncodeChat(wire);
        Assert.Equal(ChatMsgType.Chat, bytes[0]);
        var (type, json) = ChatCodec.Decode(bytes);
        Assert.Equal(ChatMsgType.Chat, type);
        var back = ChatCodec.Parse<ChatWire>(json);
        Assert.NotNull(back);
        Assert.Equal(wire.FromId, back!.FromId);
        Assert.Equal(wire.FromName, back.FromName);
        Assert.Equal(wire.Text, back.Text);
        Assert.Null(back.ToId);
        Assert.Equal(wire.UtcUnixMs, back.UtcUnixMs);
    }

    [Fact]
    public void ChatCodec_DmAndTyping()
    {
        var wire = new ChatWire
        {
            FromId = "a",
            FromName = "A",
            Text = "私信",
            ToId = "b",
            UtcUnixMs = 100
        };
        var chat = ChatCodec.EncodeChat(wire);
        var parsed = ChatCodec.Parse<ChatWire>(ChatCodec.Decode(chat).json);
        Assert.Equal("b", parsed!.ToId);

        var typing = ChatCodec.EncodeTyping("a", "b");
        Assert.Equal(ChatMsgType.Typing, typing[0]);
        var (_, json) = ChatCodec.Decode(typing);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("a", doc.RootElement.GetProperty("fromId").GetString());
        Assert.Equal("b", doc.RootElement.GetProperty("toId").GetString());
    }

    [Fact]
    public void HubConnect_Requires_AllEnvVars()
    {
        var keys = new[] { "LANHUB_TOKEN", "LANHUB_GAME_ID", "LANHUB_PLAYER_ID", "LANHUB_SDK_PORT" };
        var backup = keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var k in keys) Environment.SetEnvironmentVariable(k, null);
            Assert.False(HubConnect.TryReadLaunchEnv(out _));

            Environment.SetEnvironmentVariable("LANHUB_TOKEN", "t");
            Assert.False(HubConnect.TryReadLaunchEnv(out _));

            Environment.SetEnvironmentVariable("LANHUB_GAME_ID", ChatSession.GameId);
            Environment.SetEnvironmentVariable("LANHUB_PLAYER_ID", "p1");
            Environment.SetEnvironmentVariable("LANHUB_SDK_PORT", "37812");
            Assert.True(HubConnect.TryReadLaunchEnv(out var env));
            Assert.Equal(37812, env.Port);
            Assert.Equal(ChatSession.GameId, env.GameId);
            Assert.Equal("p1", env.PlayerId);
            Assert.Equal("t", env.Token);
        }
        finally
        {
            foreach (var (k, v) in backup)
                Environment.SetEnvironmentVariable(k, v);
        }
    }

    [Fact]
    public void Output_Has_Sdk_And_Core_Dlls()
    {
        var lanChatOut = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "LanChat", "bin", "Debug", "net8.0-windows"));
        if (!Directory.Exists(lanChatOut))
        {
            // Release fallback
            lanChatOut = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "LanChat", "bin", "Release", "net8.0-windows"));
        }

        Assert.True(Directory.Exists(lanChatOut), $"缺少输出目录: {lanChatOut}");
        Assert.True(File.Exists(Path.Combine(lanChatOut, "LanChat.dll")) ||
                    File.Exists(Path.Combine(lanChatOut, "LanChat.exe")));
        Assert.True(File.Exists(Path.Combine(lanChatOut, "LanHub.Sdk.dll")), "缺少 LanHub.Sdk.dll");
        Assert.True(File.Exists(Path.Combine(lanChatOut, "LanHub.Core.dll")), "缺少 LanHub.Core.dll");
        Assert.True(File.Exists(Path.Combine(lanChatOut, "lanhub.game.json")), "缺少 lanhub.game.json 拷贝");
    }
}
