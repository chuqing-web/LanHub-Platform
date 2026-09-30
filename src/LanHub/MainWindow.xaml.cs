using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LanHub.Core;
using LanHub.Core.Discovery;
using LanHub.Core.Localization;
using LanHub.Core.Models;
using LanHub.Core.Room;
using LanHub.Core.Storage;
using Microsoft.Win32;

namespace LanHub;

public partial class MainWindow : Window
{
    private readonly JsonStore _store = new();
    private AppSettings _settings;
    private readonly ObservableCollection<GameEntry> _games = new();
    private readonly ObservableCollection<LibraryGameItem> _libraryGames = new();
    private readonly ObservableCollection<PlayerInfo> _players = new();
    private readonly ObservableCollection<DiscoveredHost> _hosts = new();

    private RoomHostService? _host;
    private RoomClientService? _client;
    private UdpDiscoveryClient? _discovery;
    private DispatcherTimer? _discoveryTimer;
    private string? _selectedHostKey;
    private string? _selectedHostIp;
    private int _selectedHostPort;
    private string _selectedHostName = "";
    private bool _joining;
    private bool _localGameLaunched;
    private string? _lastLaunchKey;
    private Process? _localGameProcess;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _store.LoadSettings();
        foreach (var g in _store.LoadGames())
            AddLibraryGame(g);

        if (HealLibraryGameIds())
            PersistGames();

        GamesList.ItemsSource = _libraryGames;
        HostPlayersList.ItemsSource = _players;
        ClientPlayersList.ItemsSource = _players;
        HostsList.ItemsSource = _hosts;
        HostGameCombo.ItemsSource = _games;
        HostGameCombo.DisplayMemberPath = nameof(GameEntry.Title);
        if (_games.Count > 0)
            HostGameCombo.SelectedIndex = 0;

        LoadSettingsToUi();
        TxtDisplayName.Text = _settings.DisplayName;
        Loc.SetLanguage(_settings.Language);
        ApplyLanguage();
    }

    /// <summary>Rewrite random/empty GameIds from lanhub.game.json (or exe name).</summary>
    private bool HealLibraryGameIds()
    {
        var changed = false;
        foreach (var g in _games)
        {
            if (!GameIdMatcher.EnsureCanonicalFromManifest(g)) continue;
            changed = true;
            NotifyLibraryMetaChanged(g);
        }
        return changed;
    }

    /// <summary>Canonical GameId for room bind / env injection — never broadcast ephemeral ids.</summary>
    private string EnsureReadyToLaunch(GameEntry game)
    {
        if (GameIdMatcher.EnsureCanonicalFromManifest(game))
        {
            NotifyLibraryMetaChanged(game);
            PersistGames();
            RefreshHostGameCombo();
        }

        if (string.IsNullOrWhiteSpace(game.GameId) || GameIdMatcher.IsLikelyEphemeralGameId(game.GameId))
        {
            throw new InvalidOperationException(Loc.T("launch.invalid_gid"));
        }

        return game.GameId;
    }

    private void AddLibraryGame(GameEntry entry)
    {
        _games.Add(entry);
        _libraryGames.Add(new LibraryGameItem(entry));
    }

    private LibraryGameItem? SelectedLibraryItem => GamesList.SelectedItem as LibraryGameItem;
    private GameEntry? SelectedLibraryEntry => SelectedLibraryItem?.Entry;

    private void LoadSettingsToUi()
    {
        TxtSettingsName.Text = _settings.DisplayName;
        TxtUdpPort.Text = _settings.UdpPort.ToString();
        TxtTcpPort.Text = _settings.TcpPort.ToString();
        TxtSdkPort.Text = _settings.SdkPort.ToString();
        TxtGameUdpPort.Text = _settings.GameUdpPort.ToString();
        TxtMaxPlayers.Text = _settings.MaxPlayers.ToString();
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        SetNavActive(btn);
        PanelLobby.Visibility = btn == BtnNavLobby ? Visibility.Visible : Visibility.Collapsed;
        PanelLibrary.Visibility = btn == BtnNavLibrary ? Visibility.Visible : Visibility.Collapsed;
        PanelSettings.Visibility = btn == BtnNavSettings ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetNavActive(Button active)
    {
        foreach (var b in new[] { BtnNavLobby, BtnNavLibrary, BtnNavSettings })
            b.Tag = b == active ? "Active" : null;
    }

    private void ModeHost_Click(object sender, RoutedEventArgs e)
    {
        ModePicker.Visibility = Visibility.Collapsed;
        HostPanel.Visibility = Visibility.Visible;
        ClientPanel.Visibility = Visibility.Collapsed;
        RefreshHostGameCombo();
    }

    private void ModeClient_Click(object sender, RoutedEventArgs e)
    {
        ModePicker.Visibility = Visibility.Collapsed;
        HostPanel.Visibility = Visibility.Collapsed;
        ClientPanel.Visibility = Visibility.Visible;
        _localGameLaunched = false;
        _lastLaunchKey = null;
        _localGameProcess = null;
        StartDiscovery();
    }

    private void ModeHostCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        ModeHost_Click(sender, e);

    private void ModeClientCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        ModeClient_Click(sender, e);

    private void BackToMode_Click(object sender, RoutedEventArgs e)
    {
        _ = ShutdownSessionAsync();
        ModePicker.Visibility = Visibility.Visible;
        HostPanel.Visibility = Visibility.Collapsed;
        ClientPanel.Visibility = Visibility.Collapsed;
    }

    private async void BtnCreateRoom_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ShutdownSessionAsync();
            ApplyDisplayNameFromHeader();
            _host = new RoomHostService(_settings);
            _host.RosterChanged += () => Dispatcher.Invoke(RefreshHostRoster);
            _host.StatusChanged += s => Dispatcher.Invoke(() => HostStatus.Text = s);
            await _host.StartAsync(_settings.DisplayName);
            TxtRoomCode.Text = _host.RoomCode;
            RefreshHostGameCombo();
            _localGameLaunched = false;
            _lastLaunchKey = null;
            _localGameProcess = null;

            RefreshHostRoster();
            HostStatus.Text = Loc.T("host.opened");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("host.create_fail"));
        }
    }

    private void RefreshHostRoster()
    {
        _players.Clear();
        if (_host == null) return;
        foreach (var p in _host.GetRoster()) _players.Add(p);
    }

    private async void BtnCopyRoom_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtRoomCode.Text) && TxtRoomCode.Text != "------")
            Clipboard.SetText(TxtRoomCode.Text);
        await Task.CompletedTask;
    }

    private async void BtnKick_Click(object sender, RoutedEventArgs e)
    {
        if (_host == null || HostPlayersList.SelectedItem is not PlayerInfo p) return;
        await _host.KickAsync(p.PlayerId);
    }

    private async void BtnStartGame_Click(object sender, RoutedEventArgs e)
    {
        if (_host == null)
        {
            MessageBox.Show(this, Loc.T("host.need_room"));
            return;
        }

        var game = HostGameCombo.SelectedItem as GameEntry
                   ?? (_games.Count == 1 ? _games[0] : null);
        try
        {
            if (game == null)
            {
                MessageBox.Show(this, Loc.T("host.need_game"), Loc.T("host.start_fail"));
                return;
            }

            var gameId = EnsureReadyToLaunch(game);
            // Start 会广播给所有客户，触发对端自动拉起同款游戏
            await _host.StartGameAsync(gameId);
            await LaunchLibraryGameAsync(game, _host.HostPlayerId, ensureHostPrepared: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("host.start_fail"));
        }
    }

    private async void BtnDissolve_Click(object sender, RoutedEventArgs e)
    {
        if (_host != null) await _host.DissolveAsync();
        _host = null;
        TxtRoomCode.Text = "------";
        _players.Clear();
        HostStatus.Text = Loc.T("host.dissolved");
    }

    private void StartDiscovery()
    {
        _discovery?.DisposeAsync().AsTask().Wait(500);
        _discovery = new UdpDiscoveryClient(_settings);
        _discovery.Changed += () => Dispatcher.Invoke(RefreshHosts);
        _discovery.Start();
        _discoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _discoveryTimer.Tick += (_, _) => RefreshHosts();
        _discoveryTimer.Start();
    }

    private void RefreshHosts()
    {
        if (_joining) return;

        var list = (_discovery?.GetHosts() ?? Array.Empty<DiscoveredHost>()).ToList();

        // Never drop the pinned selection from the UI list (avoids flicker / deselect on Join click)
        if (!string.IsNullOrEmpty(_selectedHostIp) && _selectedHostPort > 0)
        {
            var pinKey = $"{_selectedHostIp}:{_selectedHostPort}";
            if (list.All(h => HostKey(h) != pinKey))
            {
                list.Add(new DiscoveredHost
                {
                    HostName = string.IsNullOrEmpty(_selectedHostName) ? _selectedHostIp : _selectedHostName,
                    IpAddress = _selectedHostIp!,
                    TcpPort = _selectedHostPort,
                    Joinable = true,
                    LastSeenUtc = DateTime.UtcNow
                });
            }
        }

        for (var i = _hosts.Count - 1; i >= 0; i--)
        {
            var key = HostKey(_hosts[i]);
            var isPinned = key == _selectedHostKey;
            if (!isPinned && list.All(h => HostKey(h) != key))
                _hosts.RemoveAt(i);
        }

        foreach (var h in list)
        {
            var key = HostKey(h);
            var existing = _hosts.FirstOrDefault(x => HostKey(x) == key);
            if (existing == null)
                _hosts.Add(h);
            else
            {
                existing.HostName = h.HostName;
                existing.Joinable = h.Joinable;
                existing.LastSeenUtc = h.LastSeenUtc;
                existing.IpAddress = h.IpAddress;
                existing.TcpPort = h.TcpPort;
            }
        }

        RestoreHostSelection();
    }

    private static string HostKey(DiscoveredHost h) => $"{h.IpAddress}:{h.TcpPort}";

    private void RestoreHostSelection()
    {
        if (string.IsNullOrEmpty(_selectedHostKey)) return;
        var match = _hosts.FirstOrDefault(h => HostKey(h) == _selectedHostKey);
        if (match == null) return;

        if (!ReferenceEquals(HostsList.SelectedItem, match))
        {
            HostsList.SelectionChanged -= HostsList_SelectionChanged;
            try { HostsList.SelectedItem = match; }
            finally { HostsList.SelectionChanged += HostsList_SelectionChanged; }
        }
    }

    private void HostsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HostsList.SelectedItem is DiscoveredHost h)
            PinHost(h);
        // 不要在 SelectedItem 变 null 时清掉 pin（刷新/失焦会短暂清空）
    }

    private void HostsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 在点击瞬间就钉住主机，避免随后焦点跳到「加入」导致看起来像取消选中
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem)
            dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        if (dep is ListBoxItem item && item.DataContext is DiscoveredHost h)
        {
            PinHost(h);
            item.IsSelected = true;
        }
    }

    private void PinHost(DiscoveredHost h)
    {
        _selectedHostKey = HostKey(h);
        _selectedHostIp = h.IpAddress;
        _selectedHostPort = h.TcpPort;
        _selectedHostName = h.HostName;
    }

    private bool TryGetPinnedHost(out string ip, out int port, out string name)
    {
        if (HostsList.SelectedItem is DiscoveredHost selected)
        {
            PinHost(selected);
            ip = selected.IpAddress;
            port = selected.TcpPort;
            name = selected.HostName;
            return true;
        }

        if (!string.IsNullOrEmpty(_selectedHostIp) && _selectedHostPort > 0)
        {
            ip = _selectedHostIp!;
            port = _selectedHostPort;
            name = _selectedHostName;
            return true;
        }

        ip = "";
        port = 0;
        name = "";
        return false;
    }

    private async void BtnJoin_Click(object sender, RoutedEventArgs e)
    {
        // 防连点：进行中的加入、或已在房，都直接忽略
        if (_joining) return;
        if (_client != null && !string.IsNullOrEmpty(_client.PlayerId))
        {
            MessageBox.Show(this, Loc.T("client.already_in"), Loc.T("client.already_title"));
            return;
        }

        if (!TryGetPinnedHost(out var ip, out var port, out _))
        {
            MessageBox.Show(this, Loc.T("client.pick_host"), Loc.T("client.pick_title"));
            return;
        }

        // 立刻恢复高亮，避免点「加入」时看起来被取消选中
        RestoreHostSelection();

        var code = TxtJoinCode.Text?.Trim() ?? "";
        if (code.Length != 6)
        {
            MessageBox.Show(this, Loc.T("client.need_code"));
            return;
        }

        _joining = true;
        if (BtnJoin != null) BtnJoin.IsEnabled = false;
        _discoveryTimer?.Stop();
        try
        {
            ApplyDisplayNameFromHeader();
            _localGameLaunched = false;
            _lastLaunchKey = null;
            _localGameProcess = null;
            if (_client != null)
            {
                await _client.DisposeAsync();
                _client = null;
            }

            _client = new RoomClientService(_settings);
            _client.RosterChanged += () => Dispatcher.Invoke(RefreshClientRoster);
            _client.StatusChanged += s => Dispatcher.Invoke(() =>
            {
                ClientStatus.Text = s;
                UpdateOpenBoundGameButton();
            });
            _client.GameBound += () => Dispatcher.Invoke(() =>
            {
                var gid = _client?.SelectedGameId;
                // 仅换绑其它游戏时重置；同一 GameId 的重复 GameBind/Start 不打断已启动进程
                if (!string.IsNullOrEmpty(gid) &&
                    _lastLaunchKey != null &&
                    !_lastLaunchKey.StartsWith(gid + ":", StringComparison.OrdinalIgnoreCase))
                {
                    _localGameLaunched = false;
                    _lastLaunchKey = null;
                    _localGameProcess = null;
                }

                UpdateOpenBoundGameButton();
                if (!string.IsNullOrEmpty(gid))
                    ClientStatus.Text = Loc.Tf("client.host_bound_opening", gid);
                _ = TryAutoLaunchClientGameAsync();
            });
            _client.StartReceived += (_, _) => Dispatcher.Invoke(() =>
            {
                UpdateOpenBoundGameButton();
                if (!string.IsNullOrEmpty(_client?.SelectedGameId))
                    ClientStatus.Text = Loc.Tf("client.host_started_opening", _client.SelectedGameId);
                _ = TryAutoLaunchClientGameAsync();
            });
            _client.Kicked += () => Dispatcher.Invoke(() =>
            {
                ClientStatus.Text = Loc.T("client.kicked");
                _players.Clear();
                DetachClient();
                UpdateOpenBoundGameButton();
            });
            _client.Dissolved += () => Dispatcher.Invoke(() =>
            {
                ClientStatus.Text = Loc.T("client.dissolved");
                _players.Clear();
                DetachClient();
                UpdateOpenBoundGameButton();
            });

            ClientStatus.Text = Loc.Tf("client.joining", ip, port);
            await _client.JoinAsync(ip, port, _settings.DisplayName, code);
            RefreshClientRoster();
            RestoreHostSelection();
            UpdateOpenBoundGameButton();
            if (!string.IsNullOrEmpty(_client.SelectedGameId))
            {
                ClientStatus.Text = Loc.Tf("client.joined_bound", _client.SelectedGameId);
                await TryAutoLaunchClientGameAsync();
            }
            else
                ClientStatus.Text = Loc.T("client.joined_wait");
        }
        catch (Exception ex)
        {
            if (_client != null)
            {
                await _client.DisposeAsync();
                _client = null;
            }
            UpdateOpenBoundGameButton();
            MessageBox.Show(this, ex.Message, Loc.T("client.join_fail"));
            ClientStatus.Text = Loc.T("client.join_fail");
        }
        finally
        {
            _joining = false;
            if (BtnJoin != null) BtnJoin.IsEnabled = true;
            if (_discoveryTimer != null && ClientPanel.Visibility == Visibility.Visible)
                _discoveryTimer.Start();
            RestoreHostSelection();
        }
    }

    private void DetachClient()
    {
        // RoomClientService 已在 Kick/Dissolve 时 Dispose；此处只丢掉引用，避免陈旧 Token 挡住打开
        _client = null;
        _localGameLaunched = false;
        _lastLaunchKey = null;
        _localGameProcess = null;
    }

    private void UpdateOpenBoundGameButton()
    {
        if (BtnOpenBoundGame == null) return;
        var ready = _client != null
                    && !string.IsNullOrEmpty(_client.PlayerId)
                    && !string.IsNullOrEmpty(_client.LaunchToken)
                    && !string.IsNullOrEmpty(_client.SelectedGameId);
        BtnOpenBoundGame.IsEnabled = ready;
        if (ready)
            BtnOpenBoundGame.Content = Loc.Tf("client.open_bound_named", _client!.SelectedGameId);
        else
            BtnOpenBoundGame.Content = Loc.T("client.open_bound");
    }

    private async void BtnOpenBoundGame_Click(object sender, RoutedEventArgs e)
    {
        await TryAutoLaunchClientGameAsync(force: true);
    }

    /// <summary>
    /// 客户在收到 GameBind / Start 后自动拉起本地库中匹配的游戏。
    /// </summary>
    private async Task TryAutoLaunchClientGameAsync(bool force = false)
    {
        if (_client == null) return;
        var gameId = _client.SelectedGameId;
        var token = _client.LaunchToken;
        var playerId = _client.PlayerId;
        if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(playerId))
        {
            if (force)
                MessageBox.Show(this, Loc.T("client.no_bind"), Loc.T("client.cannot_open"));
            return;
        }

        var game = ResolveLibraryGame(gameId);
        if (game == null)
        {
            if (GameIdMatcher.IsLikelyEphemeralGameId(gameId))
            {
                ClientStatus.Text = Loc.Tf("client.bad_gid_status", gameId);
                if (force)
                    MessageBox.Show(this, Loc.Tf("client.bad_gid_msg", gameId), Loc.T("client.bad_gid_title"));
                return;
            }

            ClientStatus.Text = Loc.Tf("client.missing_status", gameId);
            if (force)
                MessageBox.Show(this, Loc.Tf("client.missing_msg", gameId), Loc.T("client.missing_title"));
            return;
        }

        // 主机若仍广播短码，本地用清单纠正后再启动（SDK 已放行 ephemeral bind）
        if (GameIdMatcher.IsLikelyEphemeralGameId(gameId))
            EnsureReadyToLaunch(game);
        else if (GameIdMatcher.TryHealGameId(game, gameId))
        {
            NotifyLibraryMetaChanged(game);
            PersistGames();
            RefreshHostGameCombo();
        }

        await LaunchLibraryGameAsync(game, playerId, ensureHostPrepared: false, tokenOverride: token);
    }

    private void NotifyLibraryMetaChanged(GameEntry entry)
    {
        var item = _libraryGames.FirstOrDefault(i => ReferenceEquals(i.Entry, entry));
        item?.NotifyMetaChanged();
    }

    private void RefreshClientRoster()
    {
        _players.Clear();
        if (_client == null) return;
        foreach (var p in _client.GetRoster()) _players.Add(p);
    }

    private async void BtnReady_Click(object sender, RoutedEventArgs e)
    {
        if (_client == null) return;
        await _client.SetReadyAsync(!_client.IsReady);
        BtnReady.Content = _client.IsReady ? Loc.T("client.unready") : Loc.T("client.ready");
    }

    private async void BtnLeave_Click(object sender, RoutedEventArgs e)
    {
        if (_client != null) await _client.LeaveAsync();
        _client = null;
        _players.Clear();
        ClientStatus.Text = Loc.T("client.left");
        UpdateOpenBoundGameButton();
    }

    private GameEntry? ResolveLibraryGame(string? gameId) =>
        GameIdMatcher.FindInLibrary(_games, gameId);

    private async Task LaunchLibraryGameAsync(
        GameEntry game,
        string playerId,
        bool ensureHostPrepared,
        string? tokenOverride = null)
    {
        try
        {
            var gameId = EnsureReadyToLaunch(game);

            if (ensureHostPrepared && _host != null)
            {
                // 换绑其它游戏时允许再次启动
                if (_lastLaunchKey != null &&
                    !_lastLaunchKey.StartsWith(gameId + ":", StringComparison.OrdinalIgnoreCase))
                {
                    _localGameLaunched = false;
                    _lastLaunchKey = null;
                    _localGameProcess = null;
                }
                await _host.PrepareGameSessionAsync(gameId);
            }

            var token = tokenOverride ?? _host?.LaunchToken ?? _client?.LaunchToken;
            if (string.IsNullOrEmpty(token))
            {
                MessageBox.Show(this, Loc.T("launch.no_token"), Loc.T("launch.fail"));
                return;
            }

            if (!File.Exists(game.ExePath))
            {
                MessageBox.Show(this, Loc.Tf("launch.no_path", game.ExePath), Loc.T("launch.fail"));
                return;
            }

            var key = $"{gameId}:{playerId}";
            // 进程已退出则允许再次从游戏库拉起（崩溃/关窗后重开）
            if (_localGameLaunched && _lastLaunchKey == key &&
                _localGameProcess is { HasExited: false })
                return;
            if (_localGameProcess is { HasExited: true })
            {
                _localGameLaunched = false;
                _localGameProcess = null;
            }

            // 始终注入本机 SDK 端口，保证游戏连到本机 LanHub
            _localGameProcess = GameLauncher.StartRegisteredGame(game, playerId, token, _settings.SdkPort);
            _localGameLaunched = true;
            _lastLaunchKey = key;

            if (_host != null)
                HostStatus.Text = Loc.Tf("host.started", game.Title, gameId);
            if (_client != null)
                ClientStatus.Text = Loc.Tf("launch.client_started", game.Title, gameId);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("launch.game_fail"));
        }
    }

    private void BtnAddGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = Loc.T("dialog.exe_filter") };
        if (dlg.ShowDialog(this) != true) return;
        var entry = new GameEntry
        {
            Title = Path.GetFileNameWithoutExtension(dlg.FileName),
            ExePath = dlg.FileName,
            GameId = ""
        };

        var manifest = GameManifest.TryLoadBesideExe(dlg.FileName);
        if (manifest != null)
            manifest.ApplyTo(entry, overwriteTitle: true, overwriteArgs: true);
        else if (string.IsNullOrWhiteSpace(entry.GameId))
            entry.GameId = Path.GetFileNameWithoutExtension(dlg.FileName).ToLowerInvariant();

        AddLibraryGame(entry);
        PersistGames();
        RefreshHostGameCombo();

        if (manifest != null)
            MessageBox.Show(this,
                Loc.Tf("library.manifest_ok", entry.GameId, entry.Title),
                Loc.T("library.add_title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
    }

    private void BtnEditGame_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedLibraryItem;
        if (item == null) return;
        var win = new GameEditWindow(item.Entry) { Owner = this };
        if (win.ShowDialog() == true)
        {
            item.NotifyMetaChanged();
            PersistGames();
            RefreshHostGameCombo();
        }
    }

    private void BtnRemoveGame_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedLibraryItem;
        if (item == null) return;
        _games.Remove(item.Entry);
        _libraryGames.Remove(item);
        PersistGames();
        RefreshHostGameCombo();
    }

    private void GamesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedLibraryEntry is not { } game) return;
        OpenLibraryGame(game);
    }

    private void OpenLibraryGame(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.ExePath) || !File.Exists(game.ExePath))
        {
            MessageBox.Show(this, Loc.Tf("library.not_found", game.ExePath), Loc.T("library.open_fail"));
            return;
        }

        // 主机在房：绑定该游戏并带 Token 启动
        if (_host != null && !string.IsNullOrEmpty(_host.LaunchToken))
        {
            _ = LaunchLibraryGameAsync(game, _host.HostPlayerId, ensureHostPrepared: true);
            return;
        }

        // 客户在房：必须与主机绑定的 GameId 一致（允许宽松匹配并自动纠正）
        if (_client != null && !string.IsNullOrEmpty(_client.LaunchToken))
        {
            var playerId = _client.PlayerId;
            if (string.IsNullOrEmpty(playerId))
            {
                MessageBox.Show(this, Loc.T("library.not_in_room"), Loc.T("library.open_fail"));
                return;
            }

            if (string.IsNullOrEmpty(_client.SelectedGameId))
            {
                MessageBox.Show(this, Loc.T("library.wait_host_bind"), Loc.T("library.wait_host_title"));
                return;
            }

            var boundId = _client.SelectedGameId!;
            if (!string.Equals(boundId, game.GameId, StringComparison.OrdinalIgnoreCase))
            {
                // 主机仍在广播旧版随机 GameId：允许打开所选游戏（本机会按清单纠正）
                if (GameIdMatcher.IsLikelyEphemeralGameId(boundId))
                {
                    try { EnsureReadyToLaunch(game); }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, ex.Message, Loc.T("library.open_fail"));
                        return;
                    }
                }
                else if (GameIdMatcher.TryHealGameId(game, boundId) ||
                         GameIdMatcher.EqualsLoose(game.GameId, boundId))
                {
                    if (!string.Equals(game.GameId, boundId, StringComparison.OrdinalIgnoreCase))
                        game.GameId = boundId;
                    NotifyLibraryMetaChanged(game);
                    PersistGames();
                    RefreshHostGameCombo();
                }
                else
                {
                    MessageBox.Show(this, Loc.Tf("library.gid_mismatch", boundId), Loc.T("library.gid_mismatch_title"));
                    return;
                }
            }

            _ = LaunchLibraryGameAsync(game, playerId, ensureHostPrepared: false);
            return;
        }

        // 未在房间：本地直接打开（无联机环境变量）
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = game.ExePath,
                Arguments = game.Arguments ?? "",
                WorkingDirectory = Path.GetDirectoryName(game.ExePath) ?? Environment.CurrentDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("library.open_fail"));
        }
    }

    private void RefreshHostGameCombo()
    {
        if (HostGameCombo == null) return;

        // Keep a live binding to _games so library adds/removes always appear
        if (!ReferenceEquals(HostGameCombo.ItemsSource, _games))
            HostGameCombo.ItemsSource = _games;
        HostGameCombo.DisplayMemberPath = nameof(GameEntry.Title);

        var selectedId = (HostGameCombo.SelectedItem as GameEntry)?.GameId;
        if (!string.IsNullOrEmpty(selectedId))
        {
            var match = _games.FirstOrDefault(g => g.GameId == selectedId);
            if (match != null) HostGameCombo.SelectedItem = match;
            else if (_games.Count > 0) HostGameCombo.SelectedIndex = 0;
            else HostGameCombo.SelectedIndex = -1;
        }
        else if (_games.Count > 0 && HostGameCombo.SelectedIndex < 0)
            HostGameCombo.SelectedIndex = 0;
        else if (_games.Count == 0)
            HostGameCombo.SelectedIndex = -1;
    }

    private void PersistGames() => _store.SaveGames(_games);

    private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.DisplayName = TxtSettingsName.Text.Trim();
        if (int.TryParse(TxtUdpPort.Text, out var udp)) _settings.UdpPort = udp;
        if (int.TryParse(TxtTcpPort.Text, out var tcp)) _settings.TcpPort = tcp;
        if (int.TryParse(TxtSdkPort.Text, out var sdk)) _settings.SdkPort = sdk;
        if (int.TryParse(TxtGameUdpPort.Text, out var gudp)) _settings.GameUdpPort = gudp;
        if (int.TryParse(TxtMaxPlayers.Text, out var max))
            _settings.MaxPlayers = Math.Clamp(max, 2, 8);
        _store.SaveSettings(_settings);
        TxtDisplayName.Text = _settings.DisplayName;
        MessageBox.Show(this, Loc.T("settings.saved"));
    }

    private void Lang_Click(object sender, RoutedEventArgs e)
    {
        var lang = ReferenceEquals(sender, BtnLangEn) ? Loc.En : Loc.Zh;
        if (Loc.Language == lang) return;
        Loc.SetLanguage(lang);
        _settings.Language = lang;
        _store.SaveSettings(_settings);
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        BtnLangZh.Tag = Loc.Language == Loc.Zh ? "Active" : null;
        BtnLangEn.Tag = Loc.Language == Loc.En ? "Active" : null;

        BtnNavLobby.Content = Loc.T("nav.lobby");
        BtnNavLibrary.Content = Loc.T("nav.library");
        BtnNavSettings.Content = Loc.T("nav.settings");
        LblDisplayName.Text = Loc.T("label.display_name");

        TxtBannerEyebrow.Text = Loc.T("banner.eyebrow");
        TxtBannerTitle.Text = Loc.T("banner.title");
        TxtBannerSub.Text = Loc.T("banner.sub");
        BtnBannerHost.Content = Loc.T("banner.host");
        BtnBannerJoin.Content = Loc.T("banner.join");
        TxtShortcuts.Text = Loc.T("banner.shortcuts");
        TxtCardHostTag.Text = Loc.T("card.host_tag");
        TxtCardHostTitle.Text = Loc.T("card.host_title");
        TxtCardHostDesc.Text = Loc.T("card.host_desc");
        TxtCardClientTag.Text = Loc.T("card.client_tag");
        TxtCardClientTitle.Text = Loc.T("card.client_title");
        TxtCardClientDesc.Text = Loc.T("card.client_desc");

        BtnHostBack.Content = Loc.T("common.back");
        TxtHostTitle.Text = Loc.T("host.title");
        TxtHostRoomLabel.Text = Loc.T("host.room_code");
        BtnCreateRoom.Content = Loc.T("host.create");
        BtnCopyRoom.Content = Loc.T("host.copy");
        BtnDissolve.Content = Loc.T("host.dissolve");
        TxtSelectGame.Text = Loc.T("host.select_game");
        BtnStartGame.Content = Loc.T("host.start");
        TxtHostMembers.Text = Loc.T("host.members");
        BtnKick.Content = Loc.T("host.kick");

        BtnClientBack.Content = Loc.T("common.back");
        TxtClientTitle.Text = Loc.T("client.title");
        TxtDiscoveredHosts.Text = Loc.T("client.hosts");
        TxtClientRoomLabel.Text = Loc.T("client.room_code");
        BtnJoin.Content = Loc.T("client.join");
        BtnLeave.Content = Loc.T("client.leave");
        BtnOpenBoundGame.Content = Loc.T("client.open_bound");
        TxtClientMembers.Text = Loc.T("client.members");
        if (_client != null)
            BtnReady.Content = _client.IsReady ? Loc.T("client.unready") : Loc.T("client.ready");
        else
            BtnReady.Content = Loc.T("client.ready");

        TxtLibraryTitle.Text = Loc.T("library.title");
        TxtLibrarySub.Text = Loc.T("library.sub");
        BtnAddGame.Content = Loc.T("library.add");
        BtnEditGame.Content = Loc.T("library.edit");
        BtnRemoveGame.Content = Loc.T("library.remove");

        TxtSettingsTitle.Text = Loc.T("settings.title");
        TxtSettingsSub.Text = Loc.T("settings.sub");
        BtnSaveSettings.Content = Loc.T("settings.save");
        TxtProfileTitle.Text = Loc.T("settings.profile");
        TxtProfileHint.Text = Loc.T("settings.profile_hint");
        TxtSettingsNameLabel.Text = Loc.T("settings.display_name");
        TxtMaxPlayersTitle.Text = Loc.T("settings.max_players");
        TxtMaxPlayersHint.Text = Loc.T("settings.max_hint");
        TxtPortsTitle.Text = Loc.T("settings.ports");
        TxtPortsHint.Text = Loc.T("settings.ports_hint");
        TxtPortUdp.Text = Loc.T("settings.udp");
        TxtPortTcp.Text = Loc.T("settings.tcp");
        TxtPortSdk.Text = Loc.T("settings.sdk");
        TxtPortGameUdp.Text = Loc.T("settings.game_udp");

        UpdateOpenBoundGameButton();
        HostPlayersList.Items.Refresh();
        ClientPlayersList.Items.Refresh();
        HostsList.Items.Refresh();
        GamesList.Items.Refresh();
    }

    private void ApplyDisplayNameFromHeader()
    {
        if (!string.IsNullOrWhiteSpace(TxtDisplayName.Text))
        {
            _settings.DisplayName = TxtDisplayName.Text.Trim();
            _store.SaveSettings(_settings);
        }
    }

    private async Task ShutdownSessionAsync()
    {
        _discoveryTimer?.Stop();
        if (_discovery != null) await _discovery.DisposeAsync();
        _discovery = null;
        if (_host != null) await _host.DisposeAsync();
        _host = null;
        if (_client != null) await _client.DisposeAsync();
        _client = null;
        _players.Clear();
        // 保留已发现主机列表与选中项，方便重连
        _localGameLaunched = false;
        _lastLaunchKey = null;
        _localGameProcess = null;
        UpdateOpenBoundGameButton();
    }

    protected override async void OnClosed(EventArgs e)
    {
        await ShutdownSessionAsync();
        base.OnClosed(e);
    }
}
