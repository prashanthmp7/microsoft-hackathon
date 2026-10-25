using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using SecondHalfMind.Agents;
using SecondHalfMind.Data;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Api.Realtime;

public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, (string MatchId, ViewerProfile Profile)> _connections = new(StringComparer.Ordinal);

    public void Set(string connectionId, string matchId, ViewerProfile profile) => _connections[connectionId] = (matchId, profile);

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    public IEnumerable<IGrouping<string, (string ConnectionId, ViewerProfile Profile)>> ByProfile(string matchId) =>
        _connections
            .Where(kv => kv.Value.MatchId == matchId)
            .Select(kv => (ConnectionId: kv.Key, kv.Value.Profile))
            .GroupBy(c => c.Profile.CacheKey);
}

/// <summary>
/// Realtime channel for fans and graphics clients. Clients can only subscribe and set their profile;
/// there are no hub methods that change match data.
/// </summary>
public sealed class MatchHub : Hub
{
    private readonly ConnectionRegistry _registry;
    private readonly IMatchReadModel _matches;
    private readonly ILeagueReadModel _league;

    public MatchHub(ConnectionRegistry registry, IMatchReadModel matches, ILeagueReadModel league)
    {
        _registry = registry;
        _matches = matches;
        _league = league;
    }

    public async Task<string?> Join(string matchId, ProfileDto? profile)
    {
        var match = _matches.GetMatch(matchId);
        if (match is null)
        {
            return "Unknown match.";
        }

        if (!Validation.TryProfile(profile, match, _league, out var viewer, out var error))
        {
            return error;
        }

        _registry.Set(Context.ConnectionId, matchId, viewer);
        await Groups.AddToGroupAsync(Context.ConnectionId, matchId);
        return null;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _registry.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>Pushes snapshots, win probability and personalised overlays to connected clients.</summary>
public sealed class RealtimeBroadcaster
{
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMilliseconds(400);
    private readonly IHubContext<MatchHub> _hub;
    private readonly ConnectionRegistry _registry;
    private readonly PersonalizationAgent _personalization;
    private readonly IMatchReadModel _matches;
    private readonly ILeagueReadModel _league;
    private readonly IInsightReadModel _insights;
    private readonly IFanPredictionReadModel _fans;
    private readonly ILogger<RealtimeBroadcaster> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSnapshot = new(StringComparer.Ordinal);

    public RealtimeBroadcaster(
        IHubContext<MatchHub> hub,
        ConnectionRegistry registry,
        PersonalizationAgent personalization,
        InMemoryMatchStore store,
        InsightStore insightStore,
        ILeagueReadModel league,
        IFanPredictionReadModel fans,
        ILogger<RealtimeBroadcaster> logger)
    {
        _hub = hub;
        _registry = registry;
        _personalization = personalization;
        _matches = store;
        _league = league;
        _insights = insightStore;
        _fans = fans;
        _logger = logger;
        store.EventAppended += OnEvent;
        insightStore.Published += OnInsight;
    }

    private void OnEvent(MatchEvent e, MatchSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        var important = e.Type is MatchEventType.Goal or MatchEventType.HalfTime or MatchEventType.FullTime or MatchEventType.KickOff or MatchEventType.RedCard;
        if (!important && _lastSnapshot.TryGetValue(e.MatchId, out var last) && now - last < SnapshotInterval)
        {
            return;
        }

        _lastSnapshot[e.MatchId] = now;
        var info = _matches.GetMatch(e.MatchId);
        if (info is null)
        {
            return;
        }

        Fire(_hub.Clients.Group(e.MatchId).SendAsync("snapshot", Views.Match(info, _matches, _league, _insights)));
    }

    private void OnInsight(Insight insight)
    {
        if (insight.Kind == InsightKind.WinProbability)
        {
            Fire(_hub.Clients.Group(insight.MatchId).SendAsync("winprob", Views.WinPoint(insight)));
            return;
        }

        Fire(SendOverlaysAsync(insight));

        if (insight.Kind == InsightKind.PredictionResolved)
        {
            Fire(_hub.Clients.Group(insight.MatchId).SendAsync("leaderboard", _fans.GetLeaderboard(insight.MatchId)));
        }
    }

    private async Task SendOverlaysAsync(Insight insight)
    {
        var match = _matches.GetMatch(insight.MatchId);
        if (match is null)
        {
            return;
        }

        foreach (var group in _registry.ByProfile(insight.MatchId))
        {
            var profile = group.First().Profile;
            var overlay = await _personalization.ShapeAsync(insight, profile, match, CancellationToken.None);
            if (overlay is not null)
            {
                await _hub.Clients.Clients(group.Select(c => c.ConnectionId).ToList()).SendAsync("overlay", overlay);
            }
        }
    }

    private void Fire(Task task) =>
        task.ContinueWith(t => _logger.LogWarning(t.Exception, "Realtime push failed"), TaskContinuationOptions.OnlyOnFaulted);
}
