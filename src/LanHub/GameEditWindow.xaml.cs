using System.Windows;
using LanHub.Core.Localization;
using LanHub.Core.Models;
using Microsoft.Win32;

namespace LanHub;

public partial class GameEditWindow : Window
{
    private readonly GameEntry _entry;

    public GameEditWindow(GameEntry entry)
    {
        InitializeComponent();
        _entry = entry;
        TxtTitle.Text = entry.Title;
        TxtGameId.Text = entry.GameId;
        TxtPath.Text = entry.ExePath;
        TxtArgs.Text = entry.Arguments;
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        Title = Loc.T("edit.title");
        LblTitle.Text = Loc.T("edit.field_title");
        LblGameId.Text = Loc.T("edit.game_id");
        LblExe.Text = Loc.T("edit.exe");
        BtnBrowse.Content = Loc.T("edit.browse");
        LblArgs.Text = Loc.T("edit.args");
        BtnCancel.Content = Loc.T("edit.cancel");
        BtnSave.Content = Loc.T("edit.save");
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = Loc.T("dialog.exe_filter")
        };
        if (dlg.ShowDialog(this) == true)
            TxtPath.Text = dlg.FileName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _entry.Title = TxtTitle.Text.Trim();
        _entry.GameId = TxtGameId.Text.Trim();
        _entry.ExePath = TxtPath.Text.Trim();
        _entry.Arguments = TxtArgs.Text;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
