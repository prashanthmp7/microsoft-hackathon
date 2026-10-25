using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Data;

/// <summary>
/// Append-only match event store (reference implementation; in Azure the same contract is backed by
/// Event Hubs plus an Eventhouse in Fabric). There is deliberately no API to update or delete an event.
/// </summary>
public sealed class InMemoryMatchStore : IMatchReadModel, IMatchEventWriter, IMatchRegistryWriter, IMatchEventFeed
{
    private readonly ILeagueReadModel _league;
    private readonly ConcurrentDictionary<string, MatchRecord> _matches = new(StringComparer.Ordinal);
    private readonly Channel<MatchEvent> _feed = Channel.CreateUnbounded<MatchEvent>(new UnboundedChannelOptions { SingleReader = true });
    private int _matchCounter;

    public InMemoryMatchStore(ILeagueReadModel league)
    {
        _league = league;
    }

    /// <summary>Raised after an event is stored and projected. Used by the realtime broadcaster.</summary>
    public event Action<MatchEvent, MatchSnapshot>? EventAppended;

    public MatchInfo Register(string homeClubId, string awayClubId, int seed)
    {
        var home = _league.GetClub(homeClubId) ?? throw new ArgumentException("Unknown home club.", nameof(homeClubId));
        var away = _league.GetClub(awayClubId) ?? throw new ArgumentException("Unknown away club.", nameof(awayClubId));
        if (home.Id == away.Id)
        {
            throw new ArgumentException("A club cannot play itself.", nameof(awayClubId));
        }

        var number = Interlocked.Increment(ref _matchCounter);
        var info = new MatchInfo($"m{number:000}-{home.ShortName.ToLowerInvariant()}-{away.ShortName.ToLowerInvariant()}", home.Id, away.Id, seed, DateTimeOffset.UtcNow);
        _matches[info.Id] = new MatchRecord(info, new StateProjector(info.Id, home, away));
        return info;
    }

    public void MarkLive(string matchId) => Get(matchId).Status = MatchStatus.Live;

    public void MarkFinished(string matchId) => Get(matchId).Status = MatchStatus.Finished;

    public void Append(MatchEvent matchEvent)
    {
        var record = Get(matchEvent.MatchId);
        MatchSnapshot snapshot;
        lock (record.Gate)
        {
            // Integrity: strictly increasing sequence numbers, so events can't be replayed or reordered.
            if (matchEvent.Sequence <= record.LastSequence)
            {
                throw new InvalidOperationException($"Event {matchEvent.Id} is out of order or duplicated.");
            }

            record.Events.Add(matchEvent);
            record.LastSequence = matchEvent.Sequence;
            snapshot = record.Projector.Apply(matchEvent);
            record.Snapshot = snapshot;
        }

        _feed.Writer.TryWrite(matchEvent);
        EventAppended?.Invoke(matchEvent, snapshot);
    }

    public MatchInfo? GetMatch(string matchId) => _matches.TryGetValue(matchId, out var r) ? r.Info : null;

    public IReadOnlyList<MatchInfo> ListMatches() => _matches.Values.Select(r => r.Info).OrderBy(i => i.CreatedAt).ToList();

    public MatchStatus GetStatus(string matchId) => _matches.TryGetValue(matchId, out var r) ? r.Status : MatchStatus.Scheduled;

    public MatchSnapshot? GetSnapshot(string matchId)
    {
        if (!_matches.TryGetValue(matchId, out var record))
        {
            return null;
        }

        lock (record.Gate)
        {
            return record.Snapshot ?? record.Projector.Snapshot();
        }
    }

    public IReadOnlyList<MatchEvent> GetEvents(string matchId, long afterSequence = 0, int max = 10_000)
    {
        if (!_matches.TryGetValue(matchId, out var record))
        {
            return [];
        }

        lock (record.Gate)
        {
            // Events are immutable records, so handing out copies of the list is safe.
            return record.Events.Where(e => e.Sequence > afterSequence).Take(Math.Clamp(max, 1, 10_000)).ToList();
        }
    }

    public async IAsyncEnumerable<MatchEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var e in _feed.Reader.ReadAllAsync(cancellationToken))
        {
            yield return e;
        }
    }

    private MatchRecord Get(string matchId) =>
        _matches.TryGetValue(matchId, out var record) ? record : throw new KeyNotFoundException($"Match '{matchId}' not found.");

    private sealed class MatchRecord(MatchInfo info, StateProjector projector)
    {
        public object Gate { get; } = new();
        public MatchInfo Info { get; } = info;
        public StateProjector Projector { get; } = projector;
        public List<MatchEvent> Events { get; } = [];
        public long LastSequence { get; set; }
        public MatchSnapshot? Snapshot { get; set; }
        public MatchStatus Status { get; set; } = MatchStatus.Scheduled;
    }
}
