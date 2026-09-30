namespace LanHub.Core.Models;

/// <summary>
/// Tolerant GameId comparison / library resolution helpers.
/// Covers common drift: arena-duel vs arenaduel vs ArenaDuel,
/// and accidental random 8-hex ids from older GameEntry defaults.
/// </summary>
public static class GameIdMatcher
{
    public static string Normalize(string? gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return "";
        var chars = gameId.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars).ToLowerInvariant();
    }

    public static bool EqualsLoose(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        var na = Normalize(a);
        var nb = Normalize(b);
        return na.Length > 0 && na == nb;
    }

    /// <summary>
    /// Legacy default was Guid.NewGuid().ToString("N")[..8] — looks like "ca9c8a7f".
    /// Those ids are machine-local and break cross-player matching.
    /// </summary>
    public static bool IsLikelyEphemeralGameId(string? gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return true;
        var s = gameId.Trim();
        if (s.Length != 8) return false;
        return s.All(c => Uri.IsHexDigit(c));
    }

    /// <summary>
    /// Prefer lanhub.game.json beside the exe. Rewrites empty / ephemeral / drifted library ids.
    /// </summary>
    public static bool EnsureCanonicalFromManifest(GameEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ExePath)) return false;
        var manifest = GameManifest.TryLoadBesideExe(entry.ExePath);
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.GameId))
        {
            if (string.IsNullOrWhiteSpace(entry.GameId) || IsLikelyEphemeralGameId(entry.GameId))
            {
                var fromExe = Path.GetFileNameWithoutExtension(entry.ExePath);
                if (string.IsNullOrWhiteSpace(fromExe)) return false;
                var next = fromExe.Trim().ToLowerInvariant();
                if (string.Equals(entry.GameId, next, StringComparison.OrdinalIgnoreCase)) return false;
                entry.GameId = next;
                return true;
            }
            return false;
        }

        var changed = false;
        if (!string.Equals(entry.GameId, manifest.GameId, StringComparison.OrdinalIgnoreCase))
        {
            entry.GameId = manifest.GameId;
            changed = true;
        }

        // 纠正随机短码时一并刷新标题，便于双方游戏库显示一致
        if (!string.IsNullOrWhiteSpace(manifest.Title) &&
            (string.IsNullOrWhiteSpace(entry.Title)
             || IsLikelyEphemeralGameId(entry.GameId)
             || changed))
        {
            if (!string.Equals(entry.Title, manifest.Title, StringComparison.Ordinal))
            {
                entry.Title = manifest.Title;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Find a library entry for a host-bound GameId. Prefers exact, then manifest beside exe, then loose id / exe name.
    /// Does NOT fall back to "only one game in library" when <paramref name="wantedGameId"/> is a real id.
    /// </summary>
    public static GameEntry? FindInLibrary(IEnumerable<GameEntry> games, string? wantedGameId)
    {
        var list = games as IList<GameEntry> ?? games.ToList();
        if (list.Count == 0) return null;

        if (string.IsNullOrWhiteSpace(wantedGameId) ||
            string.Equals(wantedGameId, "probe", StringComparison.OrdinalIgnoreCase))
        {
            return list.Count == 1 ? list[0] : null;
        }

        var want = wantedGameId.Trim();
        var exact = list.FirstOrDefault(g =>
            string.Equals(g.GameId, want, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        foreach (var g in list)
        {
            if (string.IsNullOrWhiteSpace(g.ExePath)) continue;
            var m = GameManifest.TryLoadBesideExe(g.ExePath);
            if (m != null && string.Equals(m.GameId, want, StringComparison.OrdinalIgnoreCase))
                return g;
        }

        var wantNorm = Normalize(want);
        var loose = list.FirstOrDefault(g =>
        {
            if (Normalize(g.GameId) == wantNorm) return true;
            var exeName = Path.GetFileNameWithoutExtension(g.ExePath ?? "");
            if (Normalize(exeName) == wantNorm) return true;
            if (string.IsNullOrWhiteSpace(g.ExePath)) return false;
            var m = GameManifest.TryLoadBesideExe(g.ExePath);
            return m != null && Normalize(m.GameId) == wantNorm;
        });
        if (loose != null) return loose;

        // Host still broadcasting a legacy random id: match the single library game
        // that ships a real manifest (common when host forgot to fix GameId).
        if (IsLikelyEphemeralGameId(want))
        {
            var withManifest = list.Where(g =>
            {
                if (string.IsNullOrWhiteSpace(g.ExePath)) return false;
                var m = GameManifest.TryLoadBesideExe(g.ExePath);
                return m != null && !string.IsNullOrWhiteSpace(m.GameId);
            }).ToList();
            if (withManifest.Count == 1) return withManifest[0];
            if (list.Count == 1) return list[0];
        }

        return null;
    }

    /// <summary>If library GameId drifted, rewrite it to the canonical host-bound id.</summary>
    public static bool TryHealGameId(GameEntry entry, string canonicalGameId)
    {
        if (string.IsNullOrWhiteSpace(canonicalGameId)) return false;
        if (string.Equals(entry.GameId, canonicalGameId, StringComparison.OrdinalIgnoreCase))
            return false;

        // Never heal a good manifest id TOWARD an ephemeral host id
        if (IsLikelyEphemeralGameId(canonicalGameId))
        {
            EnsureCanonicalFromManifest(entry);
            return false;
        }

        var manifest = string.IsNullOrWhiteSpace(entry.ExePath)
            ? null
            : GameManifest.TryLoadBesideExe(entry.ExePath);
        var canHeal =
            (manifest != null && EqualsLoose(manifest.GameId, canonicalGameId))
            || EqualsLoose(entry.GameId, canonicalGameId)
            || EqualsLoose(Path.GetFileNameWithoutExtension(entry.ExePath ?? ""), canonicalGameId)
            || IsLikelyEphemeralGameId(entry.GameId);

        if (!canHeal) return false;
        entry.GameId = canonicalGameId.Trim();
        if (manifest != null && !string.IsNullOrWhiteSpace(manifest.Title) &&
            string.IsNullOrWhiteSpace(entry.Title))
            entry.Title = manifest.Title;
        return true;
    }
}
