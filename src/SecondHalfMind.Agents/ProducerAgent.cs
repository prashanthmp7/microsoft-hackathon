using System.Globalization;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents;

/// <summary>
/// Production stage: at half-time and full time it cuts the highlights reel from what the other
/// agents published, plus anything they proposed upstream for the same event. The cut is itself an
/// insight, so the reel has provenance in the audit log and the highlights endpoint replays the
/// producer's picks instead of re-ranking on its own.
/// </summary>
public sealed class ProducerAgent : IMatchAgent
{
    public const string Metric = "highlights";
    public const string PicksTag = "picks";

    public string Name => "producer";

    public AgentStage Stage => AgentStage.Production;

    public IEnumerable<Insight> OnEvent(AgentContext ctx)
    {
        var final = ctx.Event.Type == MatchEventType.FullTime;
        if (!final && ctx.Event.Type != MatchEventType.HalfTime)
        {
            return [];
        }

        var pool = ctx.Insights.GetInsights(ctx.Match.Id, 0, 1000).Concat(ctx.Upstream);
        var picks = HighlightReel.Rank(pool).Take(HighlightReel.MaxCount).ToList();
        if (picks.Count == 0)
        {
            return [];
        }

        var keyMoments = picks.Count(p => p.Kind == InsightKind.KeyMoment);
        var label = final ? "Full-time" : "Half-time";
        var facts = new Dictionary<string, double>
        {
            ["clips"] = picks.Count,
            ["keyMoments"] = keyMoments,
        };
        var tags = new Dictionary<string, string>
        {
            [PicksTag] = string.Join(',', picks.Select(p => p.Id)),
            ["phase"] = final ? "full-time" : "half-time",
            ["stage"] = Stage.ToString(),
        };

        var count = picks.Count.ToString(CultureInfo.InvariantCulture);
        return
        [
            InsightFactory.Create(ctx, Name, InsightKind.Recap, 3,
                new TextVariant(
                    Fmt.S($"{label} reel cut: {Fmt.Count(picks.Count, "clip")}"),
                    Fmt.S($"Ranked from the team's published moments; {Fmt.Count(keyMoments, "key moment")} included.")),
                new TextVariant(
                    Fmt.S($"Your {label.ToLowerInvariant()} highlights are ready"),
                    Fmt.S($"{count} moments picked by the producer. Open the highlights reel to replay them.")),
                facts, metric: Metric, tags: tags, suffix: final ? "ft" : "ht"),
        ];
    }
}
