namespace LanHub.Sdk;

/// <summary>Stable error codes for games to branch on.</summary>
public enum LanHubErrorCode
{
    Ok = 0,
    NotConnected = 1001,
    HandshakeFailed = 1002,
    InvalidToken = 1003,
    PayloadTooLarge = 1004,
    SendFailed = 1005,
    TimedOut = 1006,
    Reconnecting = 1007,
    SessionEnded = 1008,
    InvalidArgument = 1009,
    RateLimited = 1010,
    Unknown = 1999
}

public enum LanHubConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Failed = 4
}

/// <summary>
/// Logical channels. Transport is still local TCP + host relay in V1;
/// Unreliable may be dropped under backpressure instead of blocking the game.
/// </summary>
public static class LanHubChannel
{
    public const string Reliable = "reliable";
    public const string Unreliable = "unreliable";
    public const string Control = "control";
}

public sealed class LanHubSessionInfo
{
    public string SessionId { get; init; } = "";
    public string GameId { get; init; } = "";
    public string LocalPlayerId { get; init; } = "";
    public string HostPlayerId { get; init; } = "";
    public bool IsHost => !string.IsNullOrEmpty(LocalPlayerId) && LocalPlayerId == HostPlayerId;
    public int MaxPayloadBytes { get; init; } = 256 * 1024;
    /// <summary>Active transport hint: tcp | udp-p2p | udp-relay</summary>
    public string Transport { get; init; } = "tcp";
    public int? GameUdpPort { get; init; }
    public IReadOnlyList<LanHubPeer> Peers { get; init; } = Array.Empty<LanHubPeer>();
}

public sealed class LanHubPeer
{
    public string PlayerId { get; init; } = "";
    public string IpAddress { get; init; } = "";
    public int UdpPort { get; init; }
}

public sealed class LanHubMessage
{
    public string FromPlayerId { get; init; } = "";
    public string? TargetPlayerId { get; init; }
    public string Channel { get; init; } = LanHubChannel.Reliable;
    public bool Reliable { get; init; } = true;
    public long Sequence { get; init; }
    public byte[] Payload { get; init; } = Array.Empty<byte>();
    public string Transport { get; init; } = "tcp";

    public string AsUtf8Text() => System.Text.Encoding.UTF8.GetString(Payload);
}

public sealed class LanHubException : Exception
{
    public LanHubErrorCode Code { get; }

    public LanHubException(LanHubErrorCode code, string message) : base(message)
    {
        Code = code;
    }
}
