using System.Collections.Concurrent;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Data;

/// <summary>
/// Append-only store for agent output. Physically separate from match events: agents can add
/// insights here, and nothing else. Insights cannot be edited or removed once published.
/// </summary>
public sealed class InsightStore : IInsightSink, IInsightReadModel
{
    private const int MaxInsightsPerMatch = 5_000;
    private readonly ConcurrentDictionary<string, List<Insight>> _byMatch = new(StringComparer.Ordinal);

    public event Action<Insight>? Published;

    public void Publish(Insight insight)
    {
        var list = _byMatch.GetOrAdd(insight.MatchId, _ => []);
        lock (list)
        {
            if (list.Count >= MaxInsightsPerMatch || list.Exists(i => i.Id == insight.Id))
            {
                return;
            }

            list.Add(insight);
        }

        Published?.Invoke(insight);
    }

    public IReadOnlyList<Insight> GetInsights(string matchId, int skip = 0, int max = 500)
    {
        if (!_byMatch.TryGetValue(matchId, out var list))
        {
            return [];
        }

        lock (list)
        {
            return list.Skip(Math.Max(0, skip)).Take(Math.Clamp(max, 1, 1000)).ToList();
        }
    }
}

/// <summary>
/// Bounded, append-only audit trail of agent and guardrail decisions. Each entry carries a SHA-256
/// hash chained to the previous entry, so any edit, insertion or removal in an exported trail is
/// detectable with <see cref="Verify"/>. When a file path is given, entries are also appended to a
/// JSON-lines file so the trail survives restarts (production: Azure Monitor or Eventhouse).
/// </summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    private const int Capacity = 20_000;
    private const string Genesis = "genesis";
    private readonly ConcurrentQueue<AuditEntry> _entries = new();
    private readonly object _chainLock = new();
    private readonly string? _filePath;
    private string _lastHash = Genesis;

    public InMemoryAuditLog(string? filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
        if (_filePath is not null && Path.GetDirectoryName(Path.GetFullPath(_filePath)) is { } dir)
        {
            Directory.CreateDirectory(dir);
        }
    }

    public void Record(string actor, string action, string? matchId, string outcome, string detail)
    {
        lock (_chainLock)
        {
            var draft = new AuditEntry(DateTimeOffset.UtcNow, actor, action, matchId, outcome,
                detail.Length > 500 ? detail[..500] : detail)
            { PreviousHash = _lastHash };
            var entry = draft with { Hash = HashOf(draft) };
            _lastHash = entry.Hash;
            _entries.Enqueue(entry);

            if (_filePath is not null)
            {
                File.AppendAllText(_filePath, System.Text.Json.JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
        }

        while (_entries.Count > Capacity && _entries.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<AuditEntry> Recent(string? matchId, int max) =>
        _entries.Where(e => matchId is null || e.MatchId == matchId)
            .Reverse()
            .Take(Math.Clamp(max, 1, 1000))
            .ToList();

    /// <summary>
    /// Checks a contiguous run of entries in recorded order: each hash is correct and links to the
    /// previous entry. Returns the index of the first broken entry, or -1 when the chain is intact.
    /// </summary>
    public static int Verify(IReadOnlyList<AuditEntry> chronological)
    {
        for (var i = 0; i < chronological.Count; i++)
        {
            var e = chronological[i];
            if (e.Hash != HashOf(e) || (i > 0 && e.PreviousHash != chronological[i - 1].Hash))
            {
                return i;
            }
        }

        return -1;
    }

    private static string HashOf(AuditEntry e)
    {
        var canonical = string.Join('\u001f', e.At.ToUnixTimeMilliseconds(), e.Actor, e.Action, e.MatchId ?? string.Empty,
            e.Outcome, e.Detail, e.PreviousHash ?? Genesis);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }
}
