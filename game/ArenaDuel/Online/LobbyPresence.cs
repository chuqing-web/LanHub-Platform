namespace ArenaDuel.Online;

/// <summary>
/// Tracks which room members have an ArenaDuel process that recently sent Hello.
/// Room roster alone is not enough — both exes must be running.
/// </summary>
public sealed class LobbyPresence
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(4);

    private readonly Dictionary<string, DateTime> _present = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;

    public LobbyPresence(TimeSpan? ttl = null) => _ttl = ttl ?? DefaultTtl;

    public int Count => _present.Count;

    public bool Contains(string playerId) => _present.ContainsKey(playerId);

    public void Mark(string playerId, DateTime? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(playerId)) return;
        _present[playerId] = utcNow ?? DateTime.UtcNow;
    }

    public void Remove(string playerId) => _present.Remove(playerId);

    public void Clear() => _present.Clear();

    public IReadOnlyCollection<string> Ids => _present.Keys;

    /// <summary>Drop peers whose Hello is older than TTL.</summary>
    public int ExpireStale(DateTime? utcNow = null, string? keepPlayerId = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var removed = 0;
        foreach (var id in _present.Keys.ToList())
        {
            if (keepPlayerId != null && string.Equals(id, keepPlayerId, StringComparison.Ordinal))
                continue;
            if (now - _present[id] > _ttl)
            {
                _present.Remove(id);
                removed++;
            }
        }
        return removed;
    }

    public static bool ShouldEnterPick(int roomCount, int presentCount) =>
        roomCount >= 2 && presentCount >= 2;
}
