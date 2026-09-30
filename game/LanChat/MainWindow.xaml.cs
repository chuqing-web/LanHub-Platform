using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace LanChat;

public partial class MainWindow : Window
{
    private readonly ChatSession _session;
    private readonly DispatcherTimer _typingClear;

    public MainWindow()
    {
        InitializeComponent();
        _session = new ChatSession(Dispatcher);
        MsgList.ItemsSource = _session.Messages;
        PeerList.ItemsSource = _session.Peers;
        _session.Changed += RefreshChrome;

        _typingClear = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _typingClear.Tick += (_, _) =>
        {
            TypingText.Text = "";
            _typingClear.Stop();
        };

        Loaded += async (_, _) =>
        {
            await _session.StartAsync();
            RefreshChrome();
            InputBox.Focus();
        };
        Closed += async (_, _) => await _session.DisposeAsync();
    }

    private void RefreshChrome()
    {
        ConnLabel.Text = _session.ConnectionLabel;
        StatusLabel.Text = _session.StatusText;
        DmHintText.Text = _session.DmHint;
        BtnRetry.Visibility = _session.CanRetry ? Visibility.Visible : Visibility.Collapsed;
        // 始终允许输入；未连接时 SendAsync 会拦截并提示
        InputBox.IsEnabled = true;
        if (!string.IsNullOrEmpty(_session.TypingHint))
        {
            TypingText.Text = _session.TypingHint;
            _typingClear.Stop();
            _typingClear.Start();
        }
        Dispatcher.BeginInvoke(() => MsgScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private async void BtnSend_Click(object sender, RoutedEventArgs e) => await SendCurrentAsync();

    private async void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            await SendCurrentAsync();
        }
    }

    private async void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(InputBox.Text))
            await _session.NotifyTypingAsync();
    }

    private async Task SendCurrentAsync()
    {
        var text = InputBox.Text;
        InputBox.Text = "";
        await _session.SendAsync(text);
        InputBox.Focus();
        RefreshChrome();
    }

    private void BtnRoom_Click(object sender, RoutedEventArgs e)
    {
        _session.SetDmTarget(null);
        RefreshChrome();
    }

    private async void BtnRetry_Click(object sender, RoutedEventArgs e)
    {
        BtnRetry.IsEnabled = false;
        try
        {
            await _session.RetryConnectAsync();
        }
        finally
        {
            BtnRetry.IsEnabled = true;
            RefreshChrome();
        }
    }

    private async void BtnLocal_Click(object sender, RoutedEventArgs e)
    {
        await _session.EnterLocalPreviewAsync();
        RefreshChrome();
    }

    private void PeerList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PeerList.SelectedItem is PeerItem peer && !peer.IsSelf)
        {
            _session.SetDmTarget(peer.PlayerId);
            RefreshChrome();
            InputBox.Focus();
        }
    }
}
