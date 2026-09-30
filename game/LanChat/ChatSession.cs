using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using LanHub.Sdk;
using LanChat.Online;

namespace LanChat;

public sealed class ChatLine
{
    public string FromId { get; init; } = "";
    public string FromName { get; init; } = "";
    public string Text { get; init; } = "";
    public string? ToId { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;
    public bool IsMine { get; init; }
    public bool IsSystem { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss");
    public string Header => IsSystem ? "系统" : (IsMine ? "我" : FromName);
    public string KindTag =>
        IsSystem ? "" :
        string.IsNullOrEmpty(ToId) ? "群聊" : "私信";
}

public sealed class PeerItem : INotifyPropertyChanged
{
    public string PlayerId { get; init; } = "";
    private string _displayName = "";
    private bool _isHost;
    private bool _isSelf;

    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnPropertyChanged(); OnPropertyChanged(nameof(Label)); }
    }

    public bool IsHost
    {
        get => _isHost;
        set { _isHost = value; OnPropertyChanged(); OnPropertyChanged(nameof(Label)); }
    }

    public bool IsSelf
    {
        get => _isSelf;
        set { _isSelf = value; OnPropertyChanged(); OnPropertyChanged(nameof(Label)); }
    }

    public string Label
    {
        get
        {
            var tag = IsSelf ? "（我）" : IsHost ? "（主机）" : "";
            return $"{DisplayName}{tag}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>LanHub-backed room chat (+ optional DM).</summary>
public sealed class ChatSession : IAsyncDisposable, INotifyPropertyChanged
{
    public const string GameId = "lan-chat";

    private readonly Dispatcher _dispatcher;
    private LanHubClient? _hub;
    private HubConnect.LaunchEnv? _launchEnv;
    private bool _localMode;
    private bool _connecting;
    private bool _announceRosterChanges;
    private string _localName = Environment.UserName;
    private string? _dmTargetId;
    private DateTime _lastTypingSent = DateTime.MinValue;

    public ObservableCollection<ChatLine> Messages { get; } = [];
    public ObservableCollection<PeerItem> Peers { get; } = [];

    public string StatusText { get; private set; } = "启动中…";
    public string ConnectionLabel { get; private set; } = "未连接";
    public bool IsLocalMode => _localMode;
    public bool IsConnected => !_localMode && (_hub?.IsConnected ?? false);
    public bool IsReconnecting =>
        !_localMode && _hub?.State == LanHubConnectionState.Reconnecting;
    /// <summary>Local preview always; online only while Connected (not Reconnecting/Failed).</summary>
    public bool CanSend => _localMode || IsConnected;
    public bool CanRetry => !_localMode && !_connecting &&
                            (_hub == null ||
                             _hub.State is LanHubConnectionState.Failed or LanHubConnectionState.Disconnected);
    public bool IsHost => _localMode || (_hub?.IsHost ?? false);
    public string LocalPlayerId => _localMode ? "local-self" : (_hub?.PlayerId ?? "");
    public string? DmTargetId => _dmTargetId;
    public string DmHint => _dmTargetId == null
        ? "当前：房间群聊"
        : $"当前：私信 → {Peers.FirstOrDefault(p => p.PlayerId == _dmTargetId)?.DisplayName ?? _dmTargetId}";

    public string TypingHint { get; private set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;

    public ChatSession(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public async Task StartAsync()
    {
        if (HubConnect.TryReadLaunchEnv(out var env))
        {
            _launchEnv = env;
            await ConnectOnlineAsync(env).ConfigureAwait(true);
        }
        else
        {
            await EnterLocalPreviewAsync().ConfigureAwait(true);
        }
    }

    public async Task RetryConnectAsync()
    {
        if (_connecting) return;
        if (_launchEnv is not { } env && !HubConnect.TryReadLaunchEnv(out env))
        {
            AddSystem("缺少 LanHub 环境变量。请从 LanHub 游戏库打开本应用（GameId=lan-chat）。");
            Notify();
            return;
        }

        _launchEnv = env;
        await ConnectOnlineAsync(env).ConfigureAwait(true);
    }

    public async Task EnterLocalPreviewAsync()
    {
        await DisposeHubAsync().ConfigureAwait(true);
        _localMode = true;
        _launchEnv = null;
        _announceRosterChanges = false;
        ConnectionLabel = "本地预览";
        StatusText = "未检测到 LanHub 环境变量 — 消息仅本机可见";
        Peers.Clear();
        Peers.Add(new PeerItem { PlayerId = LocalPlayerId, DisplayName = _localName, IsSelf = true, IsHost = true });
        Peers.Add(new PeerItem { PlayerId = "local-bot", DisplayName = "演示同伴", IsSelf = false });
        AddSystem("本地预览模式。正式联机请从 LanHub 开房并选择本应用（GameId=lan-chat）。");
        Notify();
    }

    /// <summary>Sync wrapper for button handlers that already marshaled to UI.</summary>
    public void EnterLocalPreview() =>
        _ = EnterLocalPreviewAsync();

    public void SetDmTarget(string? playerId)
    {
        if (playerId == LocalPlayerId) playerId = null;
        _dmTargetId = playerId;
        OnPropertyChanged(nameof(DmTargetId));
        OnPropertyChanged(nameof(DmHint));
        Notify();
    }

    public async Task SendAsync(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        if (text.Length > 2000) text = text[..2000];

        var wire = new ChatWire
        {
            FromId = LocalPlayerId,
            FromName = ResolveLocalName(),
            Text = text,
            ToId = _dmTargetId,
            UtcUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        if (_localMode)
        {
            AppendMessage(wire, isMine: true);
            if (_dmTargetId == null || _dmTargetId == "local-bot")
            {
                await Task.Delay(350).ConfigureAwait(true);
                AppendMessage(new ChatWire
                {
                    FromId = "local-bot",
                    FromName = "演示同伴",
                    Text = $"收到：{text}",
                    ToId = _dmTargetId == null ? null : LocalPlayerId,
                    UtcUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }, isMine: false);
            }
            return;
        }

        if (!CanSend || _hub == null)
        {
            AddSystem(IsReconnecting
                ? "重连中，消息未发出。请稍候。"
                : "未连接，消息未发出。请点「重试连接」或从 LanHub 游戏库重新打开。");
            Notify();
            return;
        }

        AppendMessage(wire, isMine: true);

        try
        {
            var payload = ChatCodec.EncodeChat(wire);
            if (payload.Length > _hub.Session.MaxPayloadBytes)
            {
                AddSystem($"消息过大（{payload.Length} > {_hub.Session.MaxPayloadBytes}），未发送。");
                Notify();
                return;
            }

            if (_dmTargetId != null)
                await _hub.SendToAsync(_dmTargetId, payload, LanHubChannel.Reliable).ConfigureAwait(true);
            else
                await _hub.SendToAllAsync(payload, LanHubChannel.Reliable).ConfigureAwait(true);
        }
        catch (LanHubException ex)
        {
            AddSystem("发送失败：" + FormatSdkError(ex));
            Notify();
        }
        catch (Exception ex)
        {
            AddSystem("发送失败：" + ex.Message);
            Notify();
        }
    }

    public async Task NotifyTypingAsync()
    {
        if (_localMode || _hub == null || !_hub.IsConnected) return;
        if ((DateTime.UtcNow - _lastTypingSent).TotalSeconds < 1.2) return;
        _lastTypingSent = DateTime.UtcNow;
        try
        {
            var payload = ChatCodec.EncodeTyping(LocalPlayerId, _dmTargetId);
            if (_dmTargetId != null)
                await _hub.SendToAsync(_dmTargetId, payload, LanHubChannel.Unreliable, reliable: false)
                    .ConfigureAwait(true);
            else
                await _hub.SendUnreliableToAllAsync(payload).ConfigureAwait(true);
        }
        catch
        {
            // typing is best-effort
        }
    }

    public async ValueTask DisposeAsync() => await DisposeHubAsync().ConfigureAwait(false);

    private async Task ConnectOnlineAsync(HubConnect.LaunchEnv env)
    {
        _localMode = false;
        _connecting = true;
        _announceRosterChanges = false;
        ConnectionLabel = "连接中…";
        StatusText = "正在连接 LanHub…";
        Notify();

        await DisposeHubAsync().ConfigureAwait(true);

        var hub = new LanHubClient { AutoReconnect = true };
        BindHub(hub);
        _hub = hub;

        try
        {
            await HubConnect.ConnectWithRetryAsync(
                hub, env, s =>
                {
                    StatusText = s;
                    Notify();
                }).ConfigureAwait(true);

            ConnectionLabel = hub.IsHost ? "已连接 · 主机" : "已连接 · 成员";
            var gameWarn = !string.Equals(env.GameId, GameId, StringComparison.OrdinalIgnoreCase)
                ? $" ⚠ 库 GameId={env.GameId}，清单期望 {GameId}"
                : "";
            StatusText = $"会话 {hub.Session.SessionId} · 传输 {hub.Session.Transport}{gameWarn}";
            RefreshPeers();
            // 聊天为对等广播；主机仅作房间角色标识（非主机权威玩法）
            AddSystem(hub.IsHost
                ? $"已进入房间（主机 · GameId={env.GameId} · 成员 {hub.Players.Count}）"
                : $"已进入房间（成员 · GameId={env.GameId} · 成员 {hub.Players.Count}）");
            if (!string.IsNullOrEmpty(gameWarn))
                AddSystem($"警告：双方 GameId 必须完全一致，请在游戏库把本应用 GameId 改为 {GameId}。");
            _announceRosterChanges = true;
        }
        catch (Exception ex)
        {
            _announceRosterChanges = false;
            var detail = FormatConnectError(ex);
            ConnectionLabel = "连接失败";
            StatusText = detail;
            AddSystem("连接失败：" + detail);
            AddSystem("请确认 LanHub 已开房且从游戏库打开本应用；可点「重试连接」。");
            try { await hub.DisposeAsync().ConfigureAwait(true); } catch { /* ignore */ }
            if (ReferenceEquals(_hub, hub)) _hub = null;
        }
        finally
        {
            _connecting = false;
            Notify();
        }
    }

    private async Task DisposeHubAsync()
    {
        _announceRosterChanges = false;
        var hub = _hub;
        _hub = null;
        if (hub == null) return;
        try { await hub.DisposeAsync().ConfigureAwait(false); }
        catch { /* ignore */ }
    }

    private void BindHub(LanHubClient hub)
    {
        hub.RosterUpdated += _ => RunOnUi(RefreshPeers);
        hub.PlayerJoined += p => RunOnUi(() =>
        {
            RefreshPeers();
            if (_announceRosterChanges && p.PlayerId != hub.PlayerId)
                AddSystem($"{p.DisplayName} 加入了聊天");
        });
        hub.PlayerLeft += p => RunOnUi(() =>
        {
            RefreshPeers();
            if (_announceRosterChanges)
                AddSystem($"{p.DisplayName} 离开了");
            if (_dmTargetId == p.PlayerId) SetDmTarget(null);
        });
        hub.SessionUpdated += s => RunOnUi(() =>
        {
            if (hub.State == LanHubConnectionState.Connected)
            {
                ConnectionLabel = s.IsHost ? "已连接 · 主机" : "已连接 · 成员";
                StatusText = $"会话 {s.SessionId} · 传输 {s.Transport}";
            }
            Notify();
        });
        hub.StateChanged += s => RunOnUi(() =>
        {
            ConnectionLabel = s switch
            {
                LanHubConnectionState.Connected => hub.IsHost ? "已连接 · 主机" : "已连接 · 成员",
                LanHubConnectionState.Connecting => "连接中…",
                LanHubConnectionState.Reconnecting => "重连中…",
                LanHubConnectionState.Failed => "连接失败",
                LanHubConnectionState.Disconnected => "已断开",
                _ => s.ToString()
            };
            if (s == LanHubConnectionState.Reconnecting)
            {
                StatusText = "与本机 LanHub 重连中，暂停发送…";
                AddSystem("连接中断，正在自动重连…");
            }
            else if (s == LanHubConnectionState.Failed)
            {
                _announceRosterChanges = false;
                AddSystem("重连失败 / 会话结束。可点「重试连接」。");
            }
            else if (s == LanHubConnectionState.Disconnected)
            {
                _announceRosterChanges = false;
                AddSystem("已断开与 LanHub 的连接。");
            }
            else if (s == LanHubConnectionState.Connected)
            {
                _announceRosterChanges = true;
                StatusText = $"会话 {hub.Session.SessionId} · 传输 {hub.Session.Transport}";
            }
            Notify();
        });
        hub.Connected += () => RunOnUi(() =>
        {
            _announceRosterChanges = true;
            ConnectionLabel = hub.IsHost ? "已连接 · 主机" : "已连接 · 成员";
            RefreshPeers();
            Notify();
        });
        hub.Error += (c, m) => RunOnUi(() =>
        {
            if (c == LanHubErrorCode.Reconnecting) return;
            if (c == LanHubErrorCode.PayloadTooLarge)
                AddSystem($"错误 PayloadTooLarge: {m}（请缩短消息）");
            else if (c == LanHubErrorCode.InvalidToken)
                AddSystem("错误 InvalidToken: 请从 LanHub 游戏库重新打开本应用");
            else if (c == LanHubErrorCode.SessionEnded)
                AddSystem("会话已结束（主机解散或房间关闭）");
            else
                AddSystem($"错误 {c}: {m}");
        });
        hub.Disconnected += (c, m) => RunOnUi(() =>
        {
            if (c == LanHubErrorCode.SessionEnded)
                AddSystem($"会话结束: {m}");
            else
                AddSystem($"断开 {c}: {m}");
        });
        hub.MessageReceived += m => RunOnUi(() => OnMessage(m));
    }

    private static string FormatConnectError(Exception ex)
    {
        if (ex is LanHubException le) return FormatSdkError(le);
        return ex.Message;
    }

    private static string FormatSdkError(LanHubException ex) => ex.Code switch
    {
        LanHubErrorCode.InvalidToken =>
            "会话令牌无效（请从 LanHub 游戏库打开，勿直接双击 exe）",
        LanHubErrorCode.HandshakeFailed =>
            "SDK 握手失败（确认本机 LanHub 已开房，SDK 端口一致）",
        LanHubErrorCode.InvalidArgument =>
            "缺少 LANHUB_TOKEN / GAME_ID / PLAYER_ID 环境变量",
        LanHubErrorCode.TimedOut =>
            "连接超时或重连失败",
        LanHubErrorCode.NotConnected =>
            "尚未连接到本机 LanHub",
        LanHubErrorCode.PayloadTooLarge =>
            $"消息超过上限: {ex.Message}",
        LanHubErrorCode.SessionEnded =>
            "会话已结束",
        _ => $"{ex.Code}: {ex.Message}"
    };

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    private void OnMessage(LanHubMessage m)
    {
        if (m.Payload.Length == 0) return;
        var (type, json) = ChatCodec.Decode(m.Payload);
        if (type == ChatMsgType.Chat)
        {
            var wire = ChatCodec.Parse<ChatWire>(json);
            if (wire == null) return;
            // Prefer SDK from-id if wire is empty / spoofed
            if (string.IsNullOrEmpty(wire.FromId))
                wire.FromId = m.FromPlayerId;
            if (wire.FromId == LocalPlayerId) return;
            if (!string.IsNullOrEmpty(wire.ToId) && wire.ToId != LocalPlayerId) return;
            AppendMessage(wire, isMine: false);
            TypingHint = "";
            OnPropertyChanged(nameof(TypingHint));
        }
        else if (type == ChatMsgType.Typing)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var fromId = doc.RootElement.TryGetProperty("fromId", out var f)
                    ? f.GetString() ?? m.FromPlayerId
                    : m.FromPlayerId;
                if (fromId == LocalPlayerId) return;
                if (doc.RootElement.TryGetProperty("toId", out var toEl) &&
                    toEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var toId = toEl.GetString();
                    if (!string.IsNullOrEmpty(toId) && toId != LocalPlayerId) return;
                }
                var name = Peers.FirstOrDefault(p => p.PlayerId == fromId)?.DisplayName ?? fromId;
                TypingHint = $"{name} 正在输入…";
                OnPropertyChanged(nameof(TypingHint));
                Notify();
            }
            catch { /* ignore */ }
        }
    }

    private void RefreshPeers()
    {
        if (_hub == null) return;
        var mine = _hub.PlayerId;
        var keep = _hub.Players.Select(p => p.PlayerId).ToHashSet(StringComparer.Ordinal);
        for (var i = Peers.Count - 1; i >= 0; i--)
            if (!keep.Contains(Peers[i].PlayerId)) Peers.RemoveAt(i);

        foreach (var p in _hub.Players)
        {
            var existing = Peers.FirstOrDefault(x => x.PlayerId == p.PlayerId);
            if (existing == null)
            {
                Peers.Add(new PeerItem
                {
                    PlayerId = p.PlayerId,
                    DisplayName = string.IsNullOrWhiteSpace(p.DisplayName) ? p.PlayerId : p.DisplayName,
                    IsHost = p.IsHost,
                    IsSelf = p.PlayerId == mine
                });
            }
            else
            {
                existing.DisplayName = string.IsNullOrWhiteSpace(p.DisplayName) ? p.PlayerId : p.DisplayName;
                existing.IsHost = p.IsHost;
                existing.IsSelf = p.PlayerId == mine;
            }
        }
        Notify();
    }

    private void AppendMessage(ChatWire wire, bool isMine)
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds(wire.UtcUnixMs).LocalDateTime;
        if (wire.UtcUnixMs <= 0) time = DateTime.Now;
        Messages.Add(new ChatLine
        {
            FromId = wire.FromId,
            FromName = wire.FromName,
            Text = wire.Text,
            ToId = wire.ToId,
            Time = time,
            IsMine = isMine
        });
        Notify();
    }

    private void AddSystem(string text)
    {
        Messages.Add(new ChatLine
        {
            Text = text,
            IsSystem = true,
            Time = DateTime.Now
        });
        Notify();
    }

    private string ResolveLocalName()
    {
        if (_hub != null)
        {
            var me = _hub.GetPlayer(_hub.PlayerId);
            if (me != null && !string.IsNullOrWhiteSpace(me.DisplayName))
                return me.DisplayName;
        }
        return _localName;
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ConnectionLabel));
        OnPropertyChanged(nameof(DmHint));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsReconnecting));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(IsHost));
        OnPropertyChanged(nameof(CanRetry));
        Changed?.Invoke();
    }

    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
