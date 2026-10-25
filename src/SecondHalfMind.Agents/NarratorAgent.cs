using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents;

/// <summary>Narrative stage: half-time and full-time recaps built from the match state and earlier key moments.</summary>
public sealed class NarratorAgent : IMatchAgent
{
    public string Name => "narrator";

    public AgentStage Stage => AgentStage.Narrative;

    public IEnumerable<Insight> OnEvent(AgentContext ctx)
    {
        return ctx.Event.Type switch
        {
            MatchEventType.HalfTime => [Recap(ctx, final: false)],
            MatchEventType.FullTime => [Recap(ctx, final: true)],
            _ => [],
        };
    }

    private Insight Recap(AgentContext ctx, bool final)
    {
        var s = ctx.Snapshot;
        var home = ctx.Home;
        var away = ctx.Away;
        var possession = s.HomePossessionShare;
        var label = final ? "Full time" : "Half time";
        var score = Fmt.S($"{home.ShortName} {s.Home.Goals}-{s.Away.Goals} {away.ShortName}");

        var facts = new Dictionary<string, double>
        {
            ["homeGoals"] = s.Home.Goals,
            ["awayGoals"] = s.Away.Goals,
            ["xgHome"] = Math.Round(s.Home.ExpectedGoals, 2),
            ["xgAway"] = Math.Round(s.Away.ExpectedGoals, 2),
            ["possessionHome"] = Math.Round(possession, 2),
            ["possessionAway"] = Math.Round(1 - possession, 2),
            ["shotsHome"] = s.Home.Shots,
            ["shotsAway"] = s.Away.Shots,
            ["onTargetHome"] = s.Home.ShotsOnTarget,
            ["onTargetAway"] = s.Away.ShotsOnTarget,
            ["passAccHome"] = Math.Round(s.Home.PassAccuracy, 2),
            ["passAccAway"] = Math.Round(s.Away.PassAccuracy, 2),
        };

        var keyMoments = ctx.Insights.GetInsights(ctx.Match.Id, 0, 1000)
            .Where(i => i.Kind == InsightKind.KeyMoment)
            .ToList();

        // Build on the perception stage: the fastest strike the vision agent measured from tracking frames.
        var fastestTracked = ctx.Insights.GetInsights(ctx.Match.Id, 0, 1000)
            .Where(i => i.Agent == "vision" && i.Facts.ContainsKey("detectedSpeedKmh"))
            .OrderByDescending(i => i.Facts["detectedSpeedKmh"])
            .FirstOrDefault();
        var biggestSwing = keyMoments
            .Where(i => i.Facts.ContainsKey("wpBefore") && i.Facts.ContainsKey("wpAfter"))
            .OrderByDescending(i => Math.Abs(i.Facts["wpAfter"] - i.Facts["wpBefore"]))
            .FirstOrDefault();

        // Story of the result: did the scoreline match the chances?
        var xgDiff = s.Home.ExpectedGoals - s.Away.ExpectedGoals;
        var goalDiff = s.Home.Goals - s.Away.Goals;
        var story = (goalDiff, xgDiff) switch
        {
            ( > 0, > 0.4) => Fmt.S($"{home.Name} {(final ? "won" : "lead")} and the chances back it up."),
            ( > 0, < -0.4) => Fmt.S($"{home.Name} {(final ? "won" : "lead")} against the run of play; {away.Name} created more."),
            ( < 0, < -0.4) => Fmt.S($"{away.Name} {(final ? "won" : "lead")} and deserved it on the chances."),
            ( < 0, > 0.4) => Fmt.S($"{away.Name} {(final ? "won" : "lead")} despite {home.Name} creating more."),
            (0, _) when Math.Abs(xgDiff) > 0.6 => Fmt.S($"Level, but {(xgDiff > 0 ? home.Name : away.Name)} had the better chances."),
            (0, _) => "Level, and fair on the chances.",
            _ => Fmt.S($"A tight contest that could have gone either way."),
        };

        var swingText = string.Empty;
        if (biggestSwing is not null)
        {
            facts["swingMinute"] = biggestSwing.Minute;
            swingText = Fmt.S($" Turning point ({biggestSwing.Minute}'): {biggestSwing.Casual.Headline.TrimEnd('.', '!')}.");
        }

        var tags = new Dictionary<string, string> { ["phase"] = final ? "full-time" : "half-time" };
        var trackedText = string.Empty;
        if (fastestTracked is not null)
        {
            var kmh = fastestTracked.Facts["detectedSpeedKmh"];
            facts["trackedShotKmh"] = kmh;
            facts["trackedShotMinute"] = fastestTracked.Minute;
            tags["buildsOn"] = fastestTracked.Id;
            trackedText = Fmt.S($" Fastest tracked shot: {Fmt.N0(kmh)} km/h ({fastestTracked.Minute}').");
        }
        string? starId = null;
        if (final)
        {
            var star = s.Players.Values
                .OrderByDescending(p => p.Goals * 3 + p.Shots * 0.3 + p.PassesCompleted * 0.02 + p.Tackles * 0.3)
                .First();
            starId = star.PlayerId;
            facts["starGoals"] = star.Goals;
            facts["starPasses"] = star.PassesCompleted;
            tags["star"] = star.PlayerId;
            swingText += Fmt.S($" Player of the match: {ctx.PlayerName(star.PlayerId)} ({Fmt.Count(star.Goals, "goal")}, {Fmt.Count(star.PassesCompleted, "pass", "passes")} completed).");

            var crowd = ctx.Fans.GetCrowd(ctx.Match.Id);
            if (crowd.Picks > 0)
            {
                facts["crowdPicks"] = crowd.Picks;
                facts["crowdHome"] = crowd.HomeShare;
                swingText += Fmt.S($" {Fmt.Pct(crowd.HomeShare)}% of {Fmt.Count(crowd.Picks, "fan")} backed {home.ShortName}.");
            }
        }

        var analyst = new TextVariant(
            Fmt.S($"{label}: {score}"),
            Fmt.S($"xG {Fmt.N2(s.Home.ExpectedGoals)}-{Fmt.N2(s.Away.ExpectedGoals)}. Shots {s.Home.Shots}-{s.Away.Shots} ({s.Home.ShotsOnTarget}-{s.Away.ShotsOnTarget} on target). Possession {Fmt.Pct(possession)}%. Pass accuracy {Fmt.Pct(s.Home.PassAccuracy)}% vs {Fmt.Pct(s.Away.PassAccuracy)}%.{trackedText}{swingText}"));
        var casual = new TextVariant(
            Fmt.S($"{label}: {score}"),
            Fmt.S($"{story}{swingText}"));

        return InsightFactory.Create(ctx, Name, InsightKind.Recap, final ? 5 : 4, analyst, casual, facts,
            playerId: starId, metric: "recap", tags: tags, suffix: final ? "ft" : "ht");
    }
}
