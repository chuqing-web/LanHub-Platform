using ArenaDuel.Online;
using Xunit;

namespace ArenaDuel.Tests;

public sealed class HubConnectTests
{
    [Fact]
    public void TryReadLaunchEnv_requires_token_game_and_player()
    {
        var token = Environment.GetEnvironmentVariable("LANHUB_TOKEN");
        var game = Environment.GetEnvironmentVariable("LANHUB_GAME_ID");
        var player = Environment.GetEnvironmentVariable("LANHUB_PLAYER_ID");
        var port = Environment.GetEnvironmentVariable("LANHUB_SDK_PORT");
        try
        {
            Environment.SetEnvironmentVariable("LANHUB_TOKEN", null);
            Environment.SetEnvironmentVariable("LANHUB_GAME_ID", "arena-duel");
            Environment.SetEnvironmentVariable("LANHUB_PLAYER_ID", "p1");
            Assert.False(HubConnect.TryReadLaunchEnv(out _));

            Environment.SetEnvironmentVariable("LANHUB_TOKEN", "abc");
            Environment.SetEnvironmentVariable("LANHUB_GAME_ID", "arena-duel");
            Environment.SetEnvironmentVariable("LANHUB_PLAYER_ID", "p1");
            Environment.SetEnvironmentVariable("LANHUB_SDK_PORT", "37812");
            Assert.True(HubConnect.TryReadLaunchEnv(out var env));
            Assert.Equal(37812, env.Port);
            Assert.Equal("abc", env.Token);
            Assert.Equal("arena-duel", env.GameId);
            Assert.Equal("p1", env.PlayerId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LANHUB_TOKEN", token);
            Environment.SetEnvironmentVariable("LANHUB_GAME_ID", game);
            Environment.SetEnvironmentVariable("LANHUB_PLAYER_ID", player);
            Environment.SetEnvironmentVariable("LANHUB_SDK_PORT", port);
        }
    }

    [Fact]
    public async Task ConnectWithRetry_gives_up_after_attempts()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await HubConnect.ConnectWithRetryAsync(
                async _ =>
                {
                    attempts++;
                    await Task.Yield();
                    throw new InvalidOperationException("refused");
                },
                s => { },
                maxAttempts: 3,
                baseDelay: TimeSpan.FromMilliseconds(1),
                CancellationToken.None);
        });
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ConnectWithRetry_does_not_retry_invalid_token()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<LanHub.Sdk.LanHubException>(async () =>
        {
            await HubConnect.ConnectWithRetryAsync(
                async _ =>
                {
                    attempts++;
                    await Task.Yield();
                    throw new LanHub.Sdk.LanHubException(
                        LanHub.Sdk.LanHubErrorCode.InvalidToken, "bad token");
                },
                s => { },
                maxAttempts: 5,
                baseDelay: TimeSpan.FromMilliseconds(1),
                CancellationToken.None);
        });
        Assert.Equal(1, attempts);
    }
}
