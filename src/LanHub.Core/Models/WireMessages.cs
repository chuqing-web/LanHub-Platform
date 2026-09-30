namespace LanHub.Core.Models;

public sealed class WireEnvelope
{
    public string Type { get; set; } = "";
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
    public string? DisplayName { get; set; }
    public string? ProofBase64 { get; set; }
    public string? SaltBase64 { get; set; }
    public string? NonceBase64 { get; set; }
    public string? PlayerId { get; set; }
    public string? TargetPlayerId { get; set; }
    public string? HostPlayerId { get; set; }
    public string? SessionId { get; set; }
    public string? GameId { get; set; }
    public string? LaunchToken { get; set; }
    public string? PayloadBase64 { get; set; }
    public string? Channel { get; set; }
    public bool? Reliable { get; set; }
    public long? Sequence { get; set; }
    public bool? IsReady { get; set; }
    public bool? Joinable { get; set; }
    public int? SdkPort { get; set; }
    public int? GameUdpPort { get; set; }
    public string? IpAddress { get; set; }
    public int? MaxPayloadBytes { get; set; }
    public List<PlayerInfo>? Players { get; set; }
    public List<PeerEndpoint>? Peers { get; set; }
    public string? Transport { get; set; } // "udp-p2p" | "udp-relay" | "tcp"
}

public static class WireTypes
{
    public const string Challenge = "challenge";
    public const string Join = "join";
    public const string Welcome = "welcome";
    public const string Reject = "reject";
    public const string Roster = "roster";
    public const string Ready = "ready";
    public const string Kick = "kick";
    public const string Start = "start";
    public const string GameBind = "game.bind";
    public const string Leave = "leave";
    public const string Dissolve = "dissolve";
    public const string Chat = "chat";
    public const string SdkHello = "sdk.hello";
    public const string SdkOk = "sdk.ok";
    public const string SdkRelay = "sdk.relay";
    public const string SdkBroadcast = "sdk.broadcast";
    public const string SdkPing = "sdk.ping";
    public const string SdkPong = "sdk.pong";
    public const string SdkRoster = "sdk.roster";
    public const string SdkSession = "sdk.session";
    public const string SdkError = "sdk.error";
    public const string PeerAnnounce = "peer.announce";
    public const string PeerTable = "peer.table";
    public const string Ping = "ping";
    public const string Pong = "pong";
}

public static class SdkLimits
{
    public const int MaxPayloadBytes = 256 * 1024; // 256 KiB
    public const int UnreliableQueueSoftLimit = 64;
}

/// <summary>Payload passed from local SDK peer into room relay.</summary>
public readonly record struct SdkOutbound(
    string FromPlayerId,
    string? TargetPlayerId,
    byte[] Payload,
    string Channel,
    bool Reliable,
    long Sequence);
