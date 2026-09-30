namespace LanHub.Core;

public static class ProtocolConstants
{
    public const string ProtocolVersion = "1";
    public const int DefaultUdpPort = 37810;
    public const int DefaultTcpPort = 37811;
    public const int DefaultSdkPort = 37812;
    public const int DefaultGameUdpPort = 37813;
    public const int DefaultMaxPlayers = 8;
    public const int BeaconIntervalMs = 1000;
    public const int JoinRateLimitPerMinute = 5;
    public const int MaxUdpGamePayload = 1200;
    public static readonly byte[] UdpMagic = "LNH1"u8.ToArray();
    public static readonly byte[] GameUdpMagic = "LNHG"u8.ToArray();
}
