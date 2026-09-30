using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanHub.Core.Models;

/// <summary>
/// Optional sidecar next to a game exe so LanHub can auto-fill GameId/Title.
/// Preferred filename: lanhub.game.json (same folder as the exe).
/// </summary>
public sealed class GameManifest
{
    [JsonPropertyName("gameId")]
    public string GameId { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static GameManifest? TryLoadBesideExe(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        try
        {
            var dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(dir)) return null;

            var candidates = new[]
            {
                Path.Combine(dir, "lanhub.game.json"),
                Path.Combine(dir, Path.GetFileNameWithoutExtension(exePath) + ".lanhub.json")
            };

            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                var raw = File.ReadAllText(path);
                var m = JsonSerializer.Deserialize<GameManifest>(raw, JsonOptions);
                if (m == null) continue;
                if (string.IsNullOrWhiteSpace(m.GameId)) continue;
                m.GameId = m.GameId.Trim();
                m.Title = (m.Title ?? "").Trim();
                m.Arguments ??= "";
                return m;
            }
        }
        catch
        {
            // ignore malformed/missing manifest
        }

        return null;
    }

    public void ApplyTo(GameEntry entry, bool overwriteTitle = true, bool overwriteArgs = false)
    {
        entry.GameId = GameId;
        if (overwriteTitle && !string.IsNullOrWhiteSpace(Title))
            entry.Title = Title;
        if (overwriteArgs && !string.IsNullOrWhiteSpace(Arguments))
            entry.Arguments = Arguments;
    }
}
