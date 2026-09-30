using LanHub.Sdk;

namespace ArenaDuel.Online;

/// <summary>Launch-env detection + resilient first connect to local LanHub SDK.</summary>
public static class HubConnect
{
    public readonly record struct LaunchEnv(int Port, string Token, string GameId, string PlayerId);

    public static bool TryReadLaunchEnv(out LaunchEnv env)
    {
        env = default;
        var token = Environment.GetEnvironmentVariable("LANHUB_TOKEN");
        var gameId = Environment.GetEnvironmentVariable("LANHUB_GAME_ID");
        var playerId = Environment.GetEnvironmentVariable("LANHUB_PLAYER_ID");
        if (string.IsNullOrWhiteSpace(token) ||
            string.IsNullOrWhiteSpace(gameId) ||
            string.IsNullOrWhiteSpace(playerId))
            return false;

        var portText = Environment.GetEnvironmentVariable("LANHUB_SDK_PORT") ?? "37812";
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
            port = 37812;

        env = new LaunchEnv(port, token.Trim(), gameId.Trim(), playerId.Trim());
        return true;
    }

    /// <summary>
    /// Connect with short backoff — covers the race where the game process starts
    /// a beat before the local SDK endpoint finishes binding / GameId is set.
    /// </summary>
    public static async Task ConnectWithRetryAsync(
        LanHubClient client,
        LaunchEnv env,
        Action<string>? status = null,
        int maxAttempts = 12,
        CancellationToken ct = default)
    {
        await ConnectWithRetryAsync(
            async tokenCt => await client.ConnectAsync(env.Port, env.Token, env.GameId, env.PlayerId, tokenCt)
                .ConfigureAwait(false),
            status ?? (_ => { }),
            maxAttempts,
            TimeSpan.FromMilliseconds(200),
            ct).ConfigureAwait(false);
    }

    /// <summary>Testable retry loop used by unit tests.</summary>
    public static async Task ConnectWithRetryAsync(
        Func<CancellationToken, Task> connectOnce,
        Action<string> status,
        int maxAttempts,
        TimeSpan baseDelay,
        CancellationToken ct)
    {
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            status(attempt == 1
                ? "正在连接 LanHub…"
                : $"正在连接 LanHub…（第 {attempt}/{maxAttempts} 次）");
            try
            {
                await connectOnce(ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (LanHubException ex) when (
                ex.Code is LanHubErrorCode.InvalidToken or LanHubErrorCode.InvalidArgument)
            {
                // Token / 参数错误重试无意义
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt >= maxAttempts) break;
                var delay = TimeSpan.FromMilliseconds(Math.Min(4000, baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)));
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        throw last ?? new LanHubException(LanHubErrorCode.HandshakeFailed, "连接失败");
    }
}
