using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using LanHub.Core.Models;

namespace LanHub;

/// <summary>Game library row with extracted exe icon.</summary>
public sealed class LibraryGameItem : INotifyPropertyChanged
{
    public LibraryGameItem(GameEntry entry)
    {
        Entry = entry;
        RefreshIcon();
    }

    public GameEntry Entry { get; }

    // 必须带 set：WPF 的 Run.Text 默认 TwoWay 绑定
    public string Title
    {
        get => Entry.Title;
        set { if (Entry.Title != value) { Entry.Title = value; OnPropertyChanged(); } }
    }

    public string GameId
    {
        get => Entry.GameId;
        set { if (Entry.GameId != value) { Entry.GameId = value; OnPropertyChanged(); } }
    }

    public string ExePath
    {
        get => Entry.ExePath;
        set
        {
            if (Entry.ExePath == value) return;
            Entry.ExePath = value;
            OnPropertyChanged();
            RefreshIcon();
        }
    }

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            OnPropertyChanged();
        }
    }

    public void RefreshIcon() => Icon = GameIconLoader.TryLoadFromExe(Entry.ExePath);

    public void NotifyMetaChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(GameId));
        OnPropertyChanged(nameof(ExePath));
        RefreshIcon();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
