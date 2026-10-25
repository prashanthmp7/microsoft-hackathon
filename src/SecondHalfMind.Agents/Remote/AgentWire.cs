using System.Text.Json;
using System.Text.Json.Serialization;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents.Remote;

/// <summary>
/// Everything a remote agent needs for one event, sent as a self-contained message. A separately
/// hosted agent has no access to platform stores at all: it sees only this copy, scoped to one match.
/// </summary>
public sealed record AgentRequest(
    string RequestId,
    string Agent,
    MatchInfo Match,
    Club Home,
    Club Away,
    MatchEvent Event,
    MatchSnapshot Snapshot,
    IReadOnlyList<MatchEvent> Recent,
    PreMatchBriefing Briefing,
    CalibrationProfile Calibration,
    IReadOnlyList<Insight> Upstream,
    IReadOnlyList<Insight> MatchInsights,
    CrowdView Crowd,
    BallTrack? Track);

/// <summary>An agent's proposals for one request. The platform still checks every insight before publishing.</summary>
public sealed record AgentResponse(string RequestId, string Agent, IReadOnlyList<Insight> Insights, string? Error);

/// <summary>Sends a request to a separately hosted agent and waits for its proposals.</summary>
public interface IRemoteAgentTransport
{
    /// <summary>Returns null when the agent did not answer within <paramref name="timeout"/>.</summary>
    AgentResponse? Invoke(AgentRequest request, TimeSpan timeout);
}

public static class AgentWire
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string QueueFor(string agent) => $"agent-{agent}";

    public const string ResultsQueue = "agent-results";

    /// <summary>Copies the read-only view an in-process agent would get into a message.</summary>
    public static AgentRequest ToRequest(AgentContext ctx, string agent, BallTrack? track) => new(
        Guid.NewGuid().ToString("N"),
        agent,
        ctx.Match,
        ctx.Home,
        ctx.Away,
        ctx.Event,
        ctx.Snapshot,
        ctx.Recent,
        ctx.Briefing,
        ctx.League.Calibration,
        ctx.Upstream,
        ctx.Insights.GetInsights(ctx.Match.Id, 0, 1000),
        ctx.Fans.GetCrowd(ctx.Match.Id),
        track);

    /// <summary>Rebuilds an agent context inside the agent service from the request alone.</summary>
    public static AgentContext ToContext(AgentRequest r)
    {
        var league = new SnapshotLeague(r.Home, r.Away, r.Calibration);
        return new AgentContext(r.Match, r.Home, r.Away, r.Event, r.Snapshot, r.Recent, r.Briefing, league,
            new SnapshotInsights(r.Match.Id, r.MatchInsights), new SnapshotFans(r.Match.Id, r.Crowd))
        {
            Upstream = r.Upstream.ToList().AsReadOnly(),
        };
    }

    private sealed class SnapshotLeague(Club home, Club away, CalibrationProfile calibration) : ILeagueReadModel
    {
        public IReadOnlyList<Club> Clubs { get; } = [home, away];
        public Club? GetClub(string clubId) => Clubs.FirstOrDefault(c => c.Id == clubId);
        public Player? GetPlayer(string playerId) => Clubs.SelectMany(c => c.Squad).FirstOrDefault(p => p.Id == playerId);
        public LeagueHistory History { get; } = new([], []);
        public CalibrationProfile Calibration { get; } = calibration;
    }

    private sealed class SnapshotInsights(string matchId, IReadOnlyList<Insight> insights) : IInsightReadModel
    {
        public IReadOnlyList<Insight> GetInsights(string requested, int skip = 0, int max = 500) =>
            requested == matchId ? insights.Skip(skip).Take(max).ToList() : [];
    }

    private sealed class SnapshotFans(string matchId, CrowdView crowd) : IFanPredictionReadModel
    {
        public CrowdView GetCrowd(string requested) => requested == matchId ? crowd : new CrowdView(0, 0, 0, 0);

        public Leaderboard GetLeaderboard(string requested) =>
            new(requested, [], new SourceScore("model", 0, 0, 0), new SourceScore("pundit", 0, 0, 0));

        public MicroQuestion? GetOpenQuestion(string requested) => null;
    }
}

/// <summary>
/// Tracking for the request being handled. The vision service has no tracking store; the platform
/// sends the one ball track for the event, and this exposes it through the normal read interface.
/// </summary>
public sealed class RequestTracking : ITrackingReadModel
{
    private static readonly AsyncLocal<BallTrack?> Current = new();

    public static IDisposable Use(BallTrack? track)
    {
        Current.Value = track;
        return new Reset();
    }

    public BallTrack? GetBallTrack(string matchId, long sequence) =>
        Current.Value is { } t && t.MatchId == matchId && t.Sequence == sequence ? t : null;

    private sealed class Reset : IDisposable
    {
        public void Dispose() => Current.Value = null;
    }
}
