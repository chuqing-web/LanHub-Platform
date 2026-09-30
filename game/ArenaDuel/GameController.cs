using System.Windows;
using System.Windows.Threading;
using LanHub.Sdk;
using ArenaDuel.Data;
using ArenaDuel.Gameplay;
using ArenaDuel.Online;

namespace ArenaDuel;

public enum UiPhase
{
    Boot,
    Lobby,
    Pick,
    Battle,
    Result
}

/// <summary>Orchestrates local debug and LanHub online 1v1.</summary>
public sealed class GameController : IAsyncDisposable
{
    public const string GameId = "arena-duel";
    /// <summary>Local sim, input upload, and host snapshot broadcast rate.</summary>
    public const int TickHz = 60;
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1.0 / TickHz);

    private readonly Dispatcher _dispatcher;
    private LanHubClient? _hub;
    private CombatSim? _sim;
    private readonly Dictionary<string, PickReadyMsg> _picks = new(StringComparer.Ordinal);
    private readonly LobbyPresence _presence = new();
    private MatchStartMsg? _match;
    private MatchSnapshot? _snapshot;
    private MatchEndMsg? _result;
    private readonly InputState _localInput = new();
    private uint _inputSeq;
    private DateTime _lastTickUtc = DateTime.UtcNow;
    private DateTime _lastSendUtc = DateTime.UtcNow;
    private DateTime _lastSnapUtc = DateTime.UtcNow;
    private DateTime _lastHelloUtc = DateTime.MinValue;
    private bool _localMode;
    private string _localPlayerId = "local-p1";
    private string _localOpponentId = "local-p2";
    private string _selectedCharacterId = "akishun";
    private bool _iAmReady;
    private FighterSlot _mySlot = FighterSlot.A;
    private bool _pausedForReconnect;
    private uint _lastRemoteInputSeq;
    private DateTime _lastPeerTrafficUtc = DateTime.UtcNow;
    private const double PeerSilenceSeconds = 6;

    public UiPhase Phase { get; private set; } = UiPhase.Boot;
    public string StatusText { get; private set; } = "启动中…";
    public bool IsHost { get; private set; }
    public bool IsLocalMode => _localMode;
    public bool IsPausedForReconnect => _pausedForReconnect;
    public IReadOnlyList<CharacterDef> Characters => GameData.Characters.Characters;
    public string SelectedCharacterId => _selectedCharacterId;
    public bool IAmReady => _iAmReady;
    public MatchSnapshot? Snapshot => _snapshot;
    public MatchEndMsg? Result => _result;
    public MatchStartMsg? Match => _match;
    public string LocalPlayerId => _localMode ? _localPlayerId : (_hub?.PlayerId ?? "");
    public event Action? Changed;

    public GameController(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public async Task StartAsync()
    {
        var baseDir = AppContext.BaseDirectory;
        GameData.Load(baseDir);

        if (HubConnect.TryReadLaunchEnv(out var env))
        {
            _localMode = false;
            StatusText = "正在连接 LanHub…";
            Notify();
            var hub = new LanHubClient();
            BindHub(hub);
            _hub = hub;
            try
            {
                await HubConnect.ConnectWithRetryAsync(
                    hub, env, s => { StatusText = s; Notify(); });
                IsHost = hub.IsHost;
                if (!string.Equals(env.GameId, GameId, StringComparison.OrdinalIgnoreCase))
                    StatusText = $"已连接，但 GameId={env.GameId}（本游戏清单为 {GameId}），双方必须完全一致";
                else
                    StatusText = IsHost ? "已连接（主机）— 等待对方游戏进程" : "已连接（客户）— 等待配对";
                Phase = UiPhase.Lobby;
                MarkSelfPresent();
                await BroadcastHelloAsync();
                EvaluateLobby();
            }
            catch (Exception ex)
            {
                StatusText = "连接失败：" + ex.Message + "。请从 LanHub 游戏库打开本程序，不要直接双击 exe。";
                Phase = UiPhase.Lobby;
                try { await hub.DisposeAsync(); } catch { /* ignore */ }
                if (ReferenceEquals(_hub, hub)) _hub = null;
            }
        }
        else
        {
            _localMode = true;
            IsHost = true;
            StatusText = "本地调试模式（未检测到 LanHub 环境变量）";
            Phase = UiPhase.Lobby;
        }
        Notify();
    }

    public void StartLocalMatchSetup()
    {
        _localMode = true;
        IsHost = true;
        _picks.Clear();
        _iAmReady = false;
        _match = null;
        _result = null;
        _snapshot = null;
        Phase = UiPhase.Pick;
        StatusText = "本地盲选：选角色后点 Ready；对手将使用列表中下一个角色";
        Notify();
    }

    public void SelectCharacter(string id)
    {
        if (Phase != UiPhase.Pick || _iAmReady) return;
        _selectedCharacterId = id;
        Notify();
    }

    public async Task ToggleReadyAsync()
    {
        if (Phase != UiPhase.Pick) return;
        _iAmReady = !_iAmReady;
        var me = LocalPlayerId;
        var msg = new PickReadyMsg
        {
            PlayerId = me,
            CharacterId = _selectedCharacterId,
            Ready = _iAmReady
        };
        _picks[me] = msg;

        if (_localMode)
        {
            if (_iAmReady)
            {
                var oppChar = Characters.FirstOrDefault(c => c.Id != _selectedCharacterId)?.Id
                              ?? Characters[0].Id;
                _picks[_localOpponentId] = new PickReadyMsg
                {
                    PlayerId = _localOpponentId,
                    CharacterId = oppChar,
                    Ready = true
                };
                BeginMatchFromPicks();
            }
            Notify();
            return;
        }

        if (_hub != null && _hub.IsConnected)
            await _hub.SendToAllAsync(NetCodec.Encode(MsgType.PickReady, msg), LanHubChannel.Reliable);
        if (IsHost) TryHostStart();
        Notify();
    }

    private bool _attackPulse;

    public void SetLocalInput(InputState input)
    {
        _localInput.MoveX = input.MoveX;
        _localInput.MoveY = input.MoveY;
        // Latch attack like skills — net upload is TickHz; brief clicks must not be lost.
        if (input.Attack) _attackPulse = true;
        _localInput.Attack = input.Attack || _attackPulse;
        _localInput.Skill1 |= input.Skill1;
        _localInput.Skill2 |= input.Skill2;
        _localInput.Ultimate |= input.Ultimate;
        _localInput.AimX = input.AimX;
        _localInput.AimY = input.AimY;
        _localInput.WorldAimX = input.WorldAimX;
        _localInput.WorldAimY = input.WorldAimY;
        _localInput.HasWorldAim = input.HasWorldAim;
    }

    private void ClearInputPulses()
    {
        _localInput.Skill1 = false;
        _localInput.Skill2 = false;
        _localInput.Ultimate = false;
        _attackPulse = false;
        _localInput.Attack = false;
    }

    public void TickFrame()
    {
        if (!_localMode && Phase is UiPhase.Lobby or UiPhase.Pick)
        {
            MaybeRepeatHello();
            if (_presence.ExpireStale(keepPlayerId: LocalPlayerId) > 0)
            {
                if (Phase == UiPhase.Pick)
                {
                    Phase = UiPhase.Lobby;
                    _picks.Clear();
                    _iAmReady = false;
                    StatusText = "对方游戏进程离线，等待重新配对";
                }
                EvaluateLobby();
            }
        }

        if (Phase != UiPhase.Battle || _pausedForReconnect) return;
        var now = DateTime.UtcNow;
        if (!_localMode && (now - _lastPeerTrafficUtc).TotalSeconds > PeerSilenceSeconds)
        {
            HandlePeerSilence();
            return;
        }

        var dt = (float)(now - _lastTickUtc).TotalSeconds;
        _lastTickUtc = now;

        if (_localMode || IsHost)
        {
            if (_sim == null) return;
            _sim.SetInput(_mySlot, _localInput);
            if (_localMode)
            {
                var snap = _sim.Capture();
                var me = _mySlot == FighterSlot.A ? snap.A : snap.B;
                var aiBody = _mySlot == FighterSlot.A ? snap.B : snap.A;
                var aiSlot = _mySlot == FighterSlot.A ? FighterSlot.B : FighterSlot.A;
                var dx = me.X - aiBody.X;
                var dy = me.Y - aiBody.Y;
                _sim.SetInput(aiSlot, new InputState
                {
                    MoveX = Math.Sign(dx),
                    MoveY = Math.Sign(dy),
                    Attack = true,
                    AimX = dx,
                    AimY = dy,
                    WorldAimX = me.X,
                    WorldAimY = me.Y,
                    HasWorldAim = true,
                    Skill1 = Random.Shared.NextDouble() < 0.012,
                    Skill2 = Random.Shared.NextDouble() < 0.01,
                    Ultimate = Random.Shared.NextDouble() < 0.005
                });
            }

            _sim.Tick(dt);
            ClearInputPulses();
            _snapshot = _sim.Capture();
            if (_sim.Ended)
            {
                FinishMatch(_snapshot);
                return;
            }

            if (!_localMode && _hub != null && _hub.IsConnected && (now - _lastSnapUtc).TotalSeconds >= 1.0 / TickHz)
            {
                _lastSnapUtc = now;
                var snapBytes = NetCodec.EncodeSnapshot(_snapshot);
                // Authority frames must not depend on UDP P2P (client otherwise stays on spawn pose).
                FireAndForget(_hub.SendToAllAsync(snapBytes, LanHubChannel.Reliable));
                // Best-effort low-latency duplicate when UDP works.
                FireAndForget(_hub.SendUnreliableToAllAsync(snapBytes));
            }
        }
        else
        {
            if (_hub != null && _hub.IsConnected && (now - _lastSendUtc).TotalSeconds >= 1.0 / TickHz)
            {
                _lastSendUtc = now;
                _inputSeq++;
                var msg = new InputMsg
                {
                    PlayerId = _hub.PlayerId,
                    MoveX = _localInput.MoveX,
                    MoveY = _localInput.MoveY,
                    Attack = _localInput.Attack,
                    Skill1 = _localInput.Skill1,
                    Skill2 = _localInput.Skill2,
                    Ultimate = _localInput.Ultimate,
                    AimX = _localInput.AimX,
                    AimY = _localInput.AimY,
                    WorldAimX = _localInput.WorldAimX,
                    WorldAimY = _localInput.WorldAimY,
                    HasWorldAim = _localInput.HasWorldAim,
                    Seq = _inputSeq
                };
                var inputBytes = NetCodec.EncodeInput(msg);
                // Reliable backup so host still receives input when UDP is one-way / blocked.
                FireAndForget(_hub.SendToAllAsync(inputBytes, LanHubChannel.Reliable));
                FireAndForget(_hub.SendUnreliableToAllAsync(inputBytes));
                ClearInputPulses();
            }
        }

        Notify();
    }

    public void ReturnToPick()
    {
        _sim = null;
        _snapshot = null;
        _result = null;
        _match = null;
        _picks.Clear();
        _iAmReady = false;
        _lastRemoteInputSeq = 0;
        if (_localMode)
        {
            Phase = UiPhase.Pick;
            StatusText = "再来一局 — 请选择角色";
        }
        else
        {
            Phase = UiPhase.Lobby;
            StatusText = "再战 — 等待双方游戏进程";
            MarkSelfPresent();
            FireAndForget(BroadcastHelloAsync());
            FireAndForget(_hub?.SendToAllAsync(NetCodec.Encode(MsgType.Rematch, new HelloMsg
            {
                PlayerId = LocalPlayerId,
                GameId = GameId
            }), LanHubChannel.Reliable));
            EvaluateLobby();
        }
        Notify();
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub != null)
            await _hub.DisposeAsync();
    }

    private void BindHub(LanHubClient hub)
    {
        hub.RosterUpdated += _ => RunOnUi(EvaluateLobby);
        hub.PlayerJoined += _ => RunOnUi(() =>
        {
            FireAndForget(BroadcastHelloAsync());
            EvaluateLobby();
        });
        hub.PlayerLeft += p => RunOnUi(() => OnPlayerLeft(p.PlayerId));
        hub.SessionUpdated += s => RunOnUi(() =>
        {
            IsHost = s.IsHost;
            Notify();
        });
        hub.StateChanged += s => RunOnUi(() => OnStateChanged(s));
        hub.Connected += () => RunOnUi(() =>
        {
            IsHost = hub.IsHost;
            MarkSelfPresent();
            FireAndForget(BroadcastHelloAsync());
            EvaluateLobby();
        });
        hub.MessageReceived += m => RunOnUi(() => OnMessage(m));
        hub.Error += (c, msg) => RunOnUi(() =>
        {
            if (c == LanHubErrorCode.Reconnecting) return;
            StatusText = $"SDK 错误 {c}: {msg}";
            Notify();
        });
        hub.Disconnected += (c, msg) => RunOnUi(() =>
        {
            StatusText = $"连接断开：{msg}";
            Notify();
        });
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    private void OnStateChanged(LanHubConnectionState s)
    {
        _pausedForReconnect = s == LanHubConnectionState.Reconnecting;
        switch (s)
        {
            case LanHubConnectionState.Reconnecting:
                StatusText = "重连中…对局暂停";
                break;
            case LanHubConnectionState.Connected:
                if (Phase == UiPhase.Battle)
                    StatusText = "已重连 — 继续对战";
                MarkSelfPresent();
                FireAndForget(BroadcastHelloAsync());
                EvaluateLobby();
                break;
            case LanHubConnectionState.Failed:
            case LanHubConnectionState.Disconnected:
                StatusText = "连接中断";
                if (Phase is UiPhase.Battle or UiPhase.Pick)
                {
                    Phase = UiPhase.Lobby;
                    _sim = null;
                    _picks.Clear();
                    _iAmReady = false;
                }
                break;
        }
        Notify();
    }

    private void EvaluateLobby()
    {
        if (_localMode || Phase is UiPhase.Battle or UiPhase.Result or UiPhase.Pick) return;
        if (_hub == null) return;

        PrunePresenceToRoster();
        MarkSelfPresent();

        var room = _hub.Players.Count;
        var present = _presence.Count;
        StatusText = $"房间 {room}/2 · 游戏进程 {present}/2" + (IsHost ? "（主机）" : "（客户）");

        if (LobbyPresence.ShouldEnterPick(room, present))
        {
            Phase = UiPhase.Pick;
            StatusText = "盲选角色 — 互相不可见，选好后 Ready";
            _picks.Clear();
            _iAmReady = false;
        }
        Notify();
    }

    private void PrunePresenceToRoster()
    {
        if (_hub == null) return;
        var ids = _hub.Players.Select(p => p.PlayerId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _presence.Ids.Where(id => !ids.Contains(id)).ToList())
            _presence.Remove(id);
    }

    private void OnPlayerLeft(string playerId)
    {
        _presence.Remove(playerId);
        _picks.Remove(playerId);

        if (Phase == UiPhase.Battle && _sim != null && IsHost)
        {
            var winner = LocalPlayerId;
            _sim.ForceEndDisconnect(winner);
            _snapshot = _sim.Capture();
            FinishMatch(_snapshot);
        }
        else if (Phase is UiPhase.Pick or UiPhase.Lobby)
        {
            Phase = UiPhase.Lobby;
            StatusText = "有玩家离开，等待重新凑齐双方游戏进程";
            _picks.Clear();
            _iAmReady = false;
            Notify();
        }
    }

    private void OnMessage(LanHubMessage m)
    {
        if (m.Payload.Length < 1) return;
        if (!string.IsNullOrEmpty(m.FromPlayerId)
            && !string.Equals(m.FromPlayerId, LocalPlayerId, StringComparison.Ordinal))
            _lastPeerTrafficUtc = DateTime.UtcNow;

        var (type, json) = NetCodec.Decode(m.Payload);
        switch (type)
        {
            case MsgType.Hello:
            {
                var hello = NetCodec.Parse<HelloMsg>(json);
                if (hello == null || string.IsNullOrWhiteSpace(hello.PlayerId)) break;
                if (!string.IsNullOrEmpty(hello.GameId)
                    && !string.Equals(hello.GameId, GameId, StringComparison.OrdinalIgnoreCase))
                    break;
                _presence.Mark(hello.PlayerId);
                // Answer so late joiners learn we are also present.
                if (!string.Equals(hello.PlayerId, LocalPlayerId, StringComparison.Ordinal))
                    FireAndForget(BroadcastHelloAsync());
                EvaluateLobby();
                break;
            }
            case MsgType.Rematch:
            {
                var rematch = NetCodec.Parse<HelloMsg>(json);
                if (rematch != null && !string.IsNullOrWhiteSpace(rematch.PlayerId))
                    _presence.Mark(rematch.PlayerId);
                if (Phase is UiPhase.Result or UiPhase.Pick or UiPhase.Lobby)
                {
                    _picks.Clear();
                    _iAmReady = false;
                    _match = null;
                    _result = null;
                    _sim = null;
                    Phase = UiPhase.Lobby;
                    MarkSelfPresent();
                    FireAndForget(BroadcastHelloAsync());
                    EvaluateLobby();
                }
                break;
            }
            case MsgType.PickReady:
            {
                var pick = NetCodec.Parse<PickReadyMsg>(json);
                if (pick != null && !string.IsNullOrWhiteSpace(pick.PlayerId))
                {
                    _picks[pick.PlayerId] = pick;
                    if (IsHost) TryHostStart();
                    Notify();
                }
                break;
            }
            case MsgType.MatchStart:
            {
                var start = NetCodec.Parse<MatchStartMsg>(json);
                if (start != null) ApplyMatchStart(start);
                break;
            }
            case MsgType.Snapshot:
                if (!IsHost)
                {
                    var snap = NetCodec.ParseSnapshot(json);
                    if (snap != null)
                    {
                        _snapshot = snap;
                        if (snap.Phase == "ended")
                        {
                            _result = new MatchEndMsg
                            {
                                WinnerPlayerId = snap.WinnerPlayerId,
                                Reason = snap.EndReason,
                                HpA = snap.A.Hp,
                                HpB = snap.B.Hp
                            };
                            Phase = UiPhase.Result;
                        }
                        Notify();
                    }
                }
                break;
            case MsgType.Input:
                if (IsHost && _sim != null)
                {
                    var input = NetCodec.Parse<InputMsg>(json);
                    if (input != null) ApplyRemoteInput(input);
                }
                break;
            case MsgType.MatchEnd:
            {
                var end = NetCodec.Parse<MatchEndMsg>(json);
                if (end != null)
                {
                    _result = end;
                    Phase = UiPhase.Result;
                    Notify();
                }
                break;
            }
        }
    }

    private void ApplyRemoteInput(InputMsg msg)
    {
        if (_match == null || _sim == null) return;
        FighterSlot slot;
        if (msg.PlayerId == _match.PlayerIdA) slot = FighterSlot.A;
        else if (msg.PlayerId == _match.PlayerIdB) slot = FighterSlot.B;
        else return;
        if (msg.PlayerId == LocalPlayerId) return;
        // Drop stale/out-of-order unreliable packets.
        if (msg.Seq != 0 && msg.Seq <= _lastRemoteInputSeq) return;
        if (msg.Seq != 0) _lastRemoteInputSeq = msg.Seq;

        _sim.SetInput(slot, new InputState
        {
            MoveX = msg.MoveX,
            MoveY = msg.MoveY,
            Attack = msg.Attack,
            Skill1 = msg.Skill1,
            Skill2 = msg.Skill2,
            Ultimate = msg.Ultimate,
            AimX = msg.AimX,
            AimY = msg.AimY,
            WorldAimX = msg.WorldAimX,
            WorldAimY = msg.WorldAimY,
            HasWorldAim = msg.HasWorldAim,
            Seq = msg.Seq
        });
    }

    private void TryHostStart()
    {
        if (_hub == null || !IsHost) return;
        var ids = _hub.Players.Select(p => p.PlayerId).Take(2).ToList();
        if (ids.Count < 2) return;
        if (!ids.All(id => _picks.TryGetValue(id, out var p) && p.Ready)) return;
        BeginMatchFromPicks();
    }

    private void BeginMatchFromPicks()
    {
        string idA, idB, charA, charB;
        if (_localMode)
        {
            idA = _localPlayerId;
            idB = _localOpponentId;
            charA = _picks[idA].CharacterId;
            charB = _picks[idB].CharacterId;
        }
        else
        {
            var ids = _hub!.Players.Select(p => p.PlayerId).Take(2).OrderBy(x => x, StringComparer.Ordinal).ToList();
            idA = ids[0];
            idB = ids[1];
            charA = _picks[idA].CharacterId;
            charB = _picks[idB].CharacterId;
        }

        var arenas = GameData.Arenas.Arenas;
        var arena = arenas[Random.Shared.Next(arenas.Count)];
        var start = new MatchStartMsg
        {
            ArenaId = arena.Id,
            PlayerIdA = idA,
            PlayerIdB = idB,
            CharacterIdA = charA,
            CharacterIdB = charB,
            Seed = Random.Shared.Next()
        };

        if (!_localMode && _hub != null && IsHost && _hub.IsConnected)
            FireAndForget(_hub.SendToAllAsync(NetCodec.Encode(MsgType.MatchStart, start), LanHubChannel.Reliable));

        ApplyMatchStart(start);
    }

    private void ApplyMatchStart(MatchStartMsg start)
    {
        _match = start;
        _mySlot = start.PlayerIdA == LocalPlayerId ? FighterSlot.A : FighterSlot.B;
        _lastRemoteInputSeq = 0;
        var arena = GameData.GetArena(start.ArenaId);
        var cA = GameData.GetCharacter(start.CharacterIdA);
        var cB = GameData.GetCharacter(start.CharacterIdB);

        if (_localMode || IsHost)
            _sim = new CombatSim(arena, cA, cB, start.PlayerIdA, start.PlayerIdB);

        _snapshot = new MatchSnapshot
        {
            TimeLeft = CombatSim.MatchDuration,
            ArenaId = arena.Id,
            A = new FighterState
            {
                PlayerId = start.PlayerIdA,
                CharacterId = start.CharacterIdA,
                Hp = cA.MaxHp,
                MaxHp = cA.MaxHp,
                Mp = cA.MaxMp,
                MaxMp = cA.MaxMp,
                X = arena.SpawnA.X,
                Y = arena.SpawnA.Y,
                Radius = cA.Radius
            },
            B = new FighterState
            {
                PlayerId = start.PlayerIdB,
                CharacterId = start.CharacterIdB,
                Hp = cB.MaxHp,
                MaxHp = cB.MaxHp,
                Mp = cB.MaxMp,
                MaxMp = cB.MaxMp,
                X = arena.SpawnB.X,
                Y = arena.SpawnB.Y,
                Radius = cB.Radius
            }
        };

        Phase = UiPhase.Battle;
        StatusText = $"战场：{arena.Name}";
        _lastTickUtc = DateTime.UtcNow;
        _lastPeerTrafficUtc = DateTime.UtcNow;
        Notify();
    }

    private void HandlePeerSilence()
    {
        if (_localMode || Phase != UiPhase.Battle) return;
        if (IsHost && _sim != null)
        {
            _sim.ForceEndDisconnect(LocalPlayerId);
            _snapshot = _sim.Capture();
            FinishMatch(_snapshot);
            StatusText = "对方长时间无响应，判定获胜";
            return;
        }

        Phase = UiPhase.Result;
        _result = new MatchEndMsg
        {
            WinnerPlayerId = null,
            Reason = "Disconnect",
            HpA = _snapshot?.A.Hp ?? 0,
            HpB = _snapshot?.B.Hp ?? 0
        };
        StatusText = "与主机失去同步";
        Notify();
    }

    private void FinishMatch(MatchSnapshot snap)
    {
        _result = new MatchEndMsg
        {
            WinnerPlayerId = snap.WinnerPlayerId,
            Reason = snap.EndReason,
            HpA = snap.A.Hp,
            HpB = snap.B.Hp
        };
        Phase = UiPhase.Result;
        StatusText = "对局结束";
        if (!_localMode && IsHost && _hub != null && _hub.IsConnected)
            FireAndForget(_hub.SendToAllAsync(NetCodec.Encode(MsgType.MatchEnd, _result), LanHubChannel.Reliable));
        Notify();
    }

    private void MarkSelfPresent()
    {
        var me = LocalPlayerId;
        if (!string.IsNullOrEmpty(me))
            _presence.Mark(me);
    }

    private async Task BroadcastHelloAsync()
    {
        if (_hub == null || !_hub.IsConnected) return;
        _lastHelloUtc = DateTime.UtcNow;
        var msg = new HelloMsg { PlayerId = LocalPlayerId, GameId = GameId };
        await _hub.SendToAllAsync(NetCodec.Encode(MsgType.Hello, msg), LanHubChannel.Reliable);
    }

    private void MaybeRepeatHello()
    {
        if (_hub == null || !_hub.IsConnected) return;
        if ((DateTime.UtcNow - _lastHelloUtc).TotalSeconds < 1.5) return;
        FireAndForget(BroadcastHelloAsync());
    }

    private static void FireAndForget(Task? task)
    {
        if (task == null) return;
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
                System.Diagnostics.Debug.WriteLine(t.Exception.GetBaseException().Message);
        }, TaskScheduler.Default);
    }

    private void Notify() => Changed?.Invoke();
}
