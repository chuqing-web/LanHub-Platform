using System.Text.Json;
using LanHub.Core.Models;

namespace LanHub.Core.Storage;

public sealed class JsonStore
{
    private readonly string _root;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public JsonStore(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LanHub");
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public AppSettings LoadSettings()
    {
        var path = Path.Combine(_root, "settings.json");
        if (!File.Exists(path)) return new AppSettings();
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
    }

    public void SaveSettings(AppSettings settings)
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
    }

    public List<GameEntry> LoadGames()
    {
        var path = Path.Combine(_root, "games.json");
        if (!File.Exists(path)) return [];
        return JsonSerializer.Deserialize<List<GameEntry>>(File.ReadAllText(path), Options) ?? [];
    }

    public void SaveGames(IEnumerable<GameEntry> games)
    {
        var path = Path.Combine(_root, "games.json");
        File.WriteAllText(path, JsonSerializer.Serialize(games.ToList(), Options));
    }
}
