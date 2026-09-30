namespace LanHub.Core.Models;

public sealed class PeerEndpoint
{
    public string PlayerId { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public int UdpPort { get; set; }
}

public sealed class AppSettings
{
    public string DisplayName { get; set; } = Environment.UserName;
    public int UdpPort { get; set; } = ProtocolConstants.DefaultUdpPort;
    public int TcpPort { get; set; } = ProtocolConstants.DefaultTcpPort;
    public int SdkPort { get; set; } = ProtocolConstants.DefaultSdkPort;
    public int GameUdpPort { get; set; } = ProtocolConstants.DefaultGameUdpPort;
    public int MaxPlayers { get; set; } = ProtocolConstants.DefaultMaxPlayers;
    /// <summary>UI language: "zh" or "en".</summary>
    public string Language { get; set; } = "zh";
}

public sealed class GameEntry
{
    /// <summary>Stable id shared by all players. Prefer lanhub.game.json; never invent random Guids.</summary>
    public string GameId { get; set; } = "";
    public string Title { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string Arguments { get; set; } = "";

    public override string ToString() =>
        !string.IsNullOrWhiteSpace(Title) ? Title
        : !string.IsNullOrWhiteSpace(GameId) ? GameId
        : base.ToString() ?? "";
}

public sealed class PlayerInfo : System.ComponentModel.INotifyPropertyChanged
{
    private string _playerId = "";
    private string _displayName = "";
    private bool _isHost;
    private bool _isReady;

    public string PlayerId
    {
        get => _playerId;
        set { if (_playerId == value) return; _playerId = value; OnPropertyChanged(nameof(PlayerId)); }
    }

    public string DisplayName
    {
        get => _displayName;
        set { if (_displayName == value) return; _displayName = value; OnPropertyChanged(nameof(DisplayName)); }
    }

    public bool IsHost
    {
        get => _isHost;
        set { if (_isHost == value) return; _isHost = value; OnPropertyChanged(nameof(IsHost)); }
    }

    public bool IsReady
    {
        get => _isReady;
        set { if (_isReady == value) return; _isReady = value; OnPropertyChanged(nameof(IsReady)); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

public sealed class DiscoveredHost
{
    public string HostName { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public int TcpPort { get; set; }
    public bool Joinable { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}
