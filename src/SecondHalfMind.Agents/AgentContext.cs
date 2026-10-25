using System.Globalization;
using SecondHalfMind.Agents.Analytics;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents;

/// <summary>
/// Everything an agent may look at when handling one event. All members are immutable values or
/// read-only interfaces; nothing here can change match data.
/// </summary>
public sealed record AgentContext(
    MatchInfo Match,
    Club Home,
    Club Away,
    MatchEvent Event,
    MatchSnapshot Snapshot,
    IReadOnlyList<MatchEvent> Recent,
    PreMatchBriefing Briefing,
    ILeagueReadModel League,
    IInsightReadModel Insights,
    IFanPredictionReadModel Fans)
{
    public int Clock => Event.ClockSeconds;

    /// <summary>
    /// Insights proposed for this same event by agents in earlier stages. Lets later agents build
    /// on earlier ones (the producer sees the narrator's recap) without any shared mutable state.
    /// </summary>
    public IReadOnlyList<Insight> Upstream { get; init; } = [];

    public Club ClubFor(Side side) => side == Side.Home ? Home : Away;

    public string PlayerName(string? playerId) =>
        playerId is null ? "A player" : League.GetPlayer(playerId)?.Name ?? "A player";

    public WindowMetrics Window(int seconds) => WindowMetrics.Compute(Recent, Math.Max(0, Clock - seconds), Clock);

    public WindowMetrics Window(int fromClock, int toClock) => WindowMetrics.Compute(Recent, fromClock, toClock);

    /// <summary>All player names in this match; the output guard rejects text that names anyone else.</summary>
    public IReadOnlyCollection<string> MatchPlayerNames() => Home.Squad.Concat(Away.Squad).Select(p => p.Name).ToList();
}

/// <summary>
/// Pipeline stages, run in this order for every event. Each stage sees what earlier stages
/// proposed, so perception feeds analysis, analysis feeds interpretation, and so on.
/// </summary>
public enum AgentStage
{
    Perception,
    Analysis,
    Interpretation,
    Narrative,
    Production,
}

/// <summary>An agent reacts to one event at a time and proposes insights. It cannot write anything itself.</summary>
public interface IMatchAgent
{
    string Name { get; }

    AgentStage Stage { get; }

    IEnumerable<Insight> OnEvent(AgentContext context);
}

internal static class InsightFactory
{
    public static Insight Create(
        AgentContext ctx,
        string agent,
        InsightKind kind,
        int priority,
        TextVariant analyst,
        TextVariant casual,
        Dictionary<string, double> facts,
        Side? team = null,
        string? playerId = null,
        string? metric = null,
        Dictionary<string, string>? tags = null,
        string? suffix = null)
    {
        facts.TryAdd("minute", ctx.Event.Minute);
        return new Insight
        {
            Id = $"{ctx.Event.Id}-{agent}-{kind}{(suffix is null ? string.Empty : "-" + suffix)}".ToLowerInvariant(),
            MatchId = ctx.Match.Id,
            SourceSequence = ctx.Event.Sequence,
            ClockSeconds = ctx.Event.ClockSeconds,
            Minute = ctx.Event.Minute,
            Kind = kind,
            Agent = agent,
            Priority = Math.Clamp(priority, 1, 5),
            Team = team,
            PlayerId = playerId,
            Metric = metric,
            Analyst = analyst,
            Casual = casual,
            Facts = facts,
            Tags = tags ?? new Dictionary<string, string>(),
            GeneratedBy = "template",
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }
}

/// <summary>Culture-invariant formatting so numbers in text always match the facts they came from.</summary>
internal static class Fmt
{
    public static string Pct(double probability) => Math.Round(probability * 100).ToString("0", CultureInfo.InvariantCulture);

    public static string N0(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture);

    public static string N1(double value) => Math.Round(value, 1).ToString("0.0", CultureInfo.InvariantCulture);

    public static string N2(double value) => Math.Round(value, 2).ToString("0.00", CultureInfo.InvariantCulture);

    public static string S(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>"Ashbury Rovers'" and "Northvale City's".</summary>
    public static string Possessive(string name) => name.EndsWith('s') ? name + "'" : name + "'s";

    /// <summary>"1 goal", "2 goals".</summary>
    public static string Count(int n, string singular, string? plural = null) =>
        string.Create(CultureInfo.InvariantCulture, $"{n} {(n == 1 ? singular : plural ?? singular + "s")}");
}
