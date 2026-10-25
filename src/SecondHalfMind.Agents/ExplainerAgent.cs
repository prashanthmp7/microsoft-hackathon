using System.Collections.Concurrent;
using SecondHalfMind.Agents.Analytics;
using SecondHalfMind.Agents.Models;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents;

/// <summary>
/// Explain stage: tracks live win probability and, when it swings, works out why. Candidate causes
/// (a goal, a red card, a substitution changing the press, a pressure or territory shift, a burst of
/// shots, the clock) are scored against measured before/after windows and the strongest one is explained.
/// </summary>
public sealed class ExplainerAgent : IMatchAgent
{
    public const double SwingThreshold = 0.12;
    private const int MinSecondsBetweenSwings = 180;
    private const int MinClockForSwings = 600;
    private const int PressureCheckSeconds = 300;
    private const double PressureShiftThreshold = 0.2;
    private const double PressureShareToReport = 0.6;
    private const int MinPressureEvents = 8;

    private readonly ConcurrentDictionary<string, MatchMemory> _memory = new(StringComparer.Ordinal);

    public string Name => "explainer";

    public AgentStage Stage => AgentStage.Interpretation;

    public IEnumerable<Insight> OnEvent(AgentContext ctx)
    {
        var memory = _memory.GetOrAdd(ctx.Match.Id, _ => new MatchMemory());
        var e = ctx.Event;
        var output = new List<Insight>();

        if (ctx.Snapshot.Phase is MatchPhase.PreMatch)
        {
            return output;
        }

        var wp = InPlayModel.WinProbability(ctx.Snapshot, ctx.Briefing.ExpectedHomeGoals, ctx.Briefing.ExpectedAwayGoals, ctx.Window(600));
        var before = memory.LastWp ?? new OutcomeProbabilities(ctx.Briefing.Model.Probabilities!.Home, ctx.Briefing.Model.Probabilities.Draw, ctx.Briefing.Model.Probabilities.Away);
        memory.Announced ??= before;

        if (e.Type == MatchEventType.Substitution)
        {
            memory.Substitutions.Add((e.ClockSeconds, e.Team, e.PlayerId));
        }

        if (e.Type == MatchEventType.Goal)
        {
            output.Add(Goal(ctx, before, wp));
            memory.Announced = wp;
            memory.LastSwingClock = e.ClockSeconds;
        }
        else if (e.Type == MatchEventType.RedCard)
        {
            output.Add(RedCard(ctx, before, wp));
            memory.Announced = wp;
            memory.LastSwingClock = e.ClockSeconds;
        }
        else if (e.ClockSeconds >= MinClockForSwings
                 && e.ClockSeconds - memory.LastSwingClock >= MinSecondsBetweenSwings
                 && e.Type is not (MatchEventType.HalfTime or MatchEventType.FullTime))
        {
            // Only a team whose own chance of winning has risen counts as a swing. A draw becoming
            // likelier as the clock runs down is expected, not news.
            var announced = memory.Announced;
            var swingHome = wp.Home - announced.Home;
            var swingAway = wp.Away - announced.Away;
            var gainer = swingHome >= swingAway ? Side.Home : Side.Away;
            if (Math.Max(swingHome, swingAway) >= SwingThreshold)
            {
                output.Add(Swing(ctx, memory, gainer, announced, wp));
                memory.Announced = wp;
                memory.LastSwingClock = e.ClockSeconds;
            }
        }

        if (e.Minute != memory.LastWpMinute || e.Type is MatchEventType.Goal or MatchEventType.RedCard or MatchEventType.FullTime)
        {
            memory.LastWpMinute = e.Minute;
            output.Add(InsightFactory.Create(ctx, Name, InsightKind.WinProbability, 1,
                new TextVariant("Win probability", Fmt.S($"{ctx.Home.ShortName} {Fmt.Pct(wp.Home)}%, draw {Fmt.Pct(wp.Draw)}%, {ctx.Away.ShortName} {Fmt.Pct(wp.Away)}%")),
                new TextVariant("Win probability", Fmt.S($"{ctx.Home.ShortName} {Fmt.Pct(wp.Home)}%, draw {Fmt.Pct(wp.Draw)}%, {ctx.Away.ShortName} {Fmt.Pct(wp.Away)}%")),
                new Dictionary<string, double> { ["home"] = wp.Home, ["draw"] = wp.Draw, ["away"] = wp.Away },
                metric: "winProbability", suffix: "wp"));
        }

        if (ctx.Snapshot.Phase is MatchPhase.FirstHalf or MatchPhase.SecondHalf
            && e.ClockSeconds >= 2 * PressureCheckSeconds
            && e.ClockSeconds - memory.LastPressureCheck >= PressureCheckSeconds)
        {
            memory.LastPressureCheck = e.ClockSeconds - e.ClockSeconds % PressureCheckSeconds;
            if (PressureShift(ctx) is { } shift)
            {
                output.Add(shift);
            }
        }

        memory.LastWp = wp;
        return output;
    }

    private Insight Goal(AgentContext ctx, OutcomeProbabilities before, OutcomeProbabilities after)
    {
        var e = ctx.Event;
        var club = ctx.ClubFor(e.Team);
        var scorer = ctx.PlayerName(e.PlayerId);
        var assist = e.TargetPlayerId is null ? null : ctx.PlayerName(e.TargetPlayerId);
        var xg = e.ExpectedGoals ?? 0;
        var s = ctx.Snapshot;
        var wpBefore = before.For(e.Team);
        var wpAfter = after.For(e.Team);
        var assistText = assist is null ? string.Empty : $", assisted by {assist}";
        var quality = xg >= 0.3 ? "a big chance taken" : xg <= 0.08 ? "a low-probability finish" : "a well-taken chance";

        return InsightFactory.Create(ctx, Name, InsightKind.KeyMoment, 5,
            new TextVariant(
                Fmt.S($"GOAL {club.ShortName}: {scorer} {e.Minute}'"),
                Fmt.S($"{ctx.Home.ShortName} {s.Home.Goals}-{s.Away.Goals} {ctx.Away.ShortName}{assistText}. xG {Fmt.N2(xg)}, {quality}. {club.ShortName} win probability {Fmt.Pct(wpBefore)}% to {Fmt.Pct(wpAfter)}%.")),
            new TextVariant(
                Fmt.S($"GOAL! {scorer} scores for {club.Name}"),
                Fmt.S($"It's {ctx.Home.ShortName} {s.Home.Goals}-{s.Away.Goals} {ctx.Away.ShortName}. A chance like that goes in {Fmt.Pct(xg)}% of the time. {club.ShortName} now {Fmt.Pct(wpAfter)}% to win, up from {Fmt.Pct(wpBefore)}%.")),
            new Dictionary<string, double>
            {
                ["xg"] = Math.Round(xg, 2),
                ["wpBefore"] = wpBefore,
                ["wpAfter"] = wpAfter,
                ["homeGoals"] = s.Home.Goals,
                ["awayGoals"] = s.Away.Goals,
            },
            e.Team, e.PlayerId, "goal",
            new Dictionary<string, string> { ["cause"] = "goal" });
    }

    private Insight RedCard(AgentContext ctx, OutcomeProbabilities before, OutcomeProbabilities after)
    {
        var e = ctx.Event;
        var club = ctx.ClubFor(e.Team);
        var other = e.Team == Side.Home ? Side.Away : Side.Home;
        var otherClub = ctx.ClubFor(other);
        var name = ctx.PlayerName(e.PlayerId);
        return InsightFactory.Create(ctx, Name, InsightKind.KeyMoment, 5,
            new TextVariant(
                Fmt.S($"Red card: {name} ({club.ShortName})"),
                Fmt.S($"{club.ShortName} down to 10. {otherClub.ShortName} win probability {Fmt.Pct(before.For(other))}% to {Fmt.Pct(after.For(other))}%.")),
            new TextVariant(
                Fmt.S($"{name} is sent off!"),
                Fmt.S($"{club.Name} have to play with 10. That lifts {Fmt.Possessive(otherClub.Name)} chances to {Fmt.Pct(after.For(other))}%.")),
            new Dictionary<string, double> { ["wpBefore"] = before.For(other), ["wpAfter"] = after.For(other), ["players"] = 10 },
            e.Team, e.PlayerId, "discipline",
            new Dictionary<string, string> { ["cause"] = "red-card" });
    }

    private Insight Swing(AgentContext ctx, MatchMemory memory, Side gainer, OutcomeProbabilities from, OutcomeProbabilities to)
    {
        var club = ctx.ClubFor(gainer);
        var now = ctx.Clock;
        var recent = ctx.Window(600);
        var earlier = ctx.Window(Math.Max(0, now - 1200), Math.Max(0, now - 600));
        var wpFrom = from.For(gainer);
        var wpTo = to.For(gainer);
        var press = recent.PressureShare(gainer);
        var pressEarlier = earlier.PressureShare(gainer);
        var passShare = recent.PassShare(gainer);
        var facts = new Dictionary<string, double>
        {
            ["wpBefore"] = wpFrom,
            ["wpAfter"] = wpTo,
            ["windowMinutes"] = 10,
            ["pressShare"] = Math.Round(press, 2),
            ["pressShareEarlier"] = Math.Round(pressEarlier, 2),
            ["passShare"] = Math.Round(passShare, 2),
        };

        // Candidate causes, each with a strength score; the strongest is explained.
        var candidates = new List<(double Score, string Cause, string Analyst, string Casual)>();

        var sub = memory.Substitutions.LastOrDefault(s => s.Team == gainer && now - s.Clock <= 600);
        if (sub.PlayerId is not null)
        {
            var subMinute = MatchEvent.ToMinute(sub.Clock);
            var beforeSub = ctx.Window(Math.Max(0, sub.Clock - 600), sub.Clock);
            var afterSub = ctx.Window(sub.Clock, now);
            var pressBefore = beforeSub.PressureShare(gainer);
            var pressAfter = afterSub.PressureShare(gainer);
            if (pressAfter - pressBefore >= 0.08)
            {
                var name = ctx.PlayerName(sub.PlayerId);
                facts["subMinute"] = subMinute;
                facts["pressBefore"] = Math.Round(pressBefore, 2);
                facts["pressAfter"] = Math.Round(pressAfter, 2);
                candidates.Add((1.0 + (pressAfter - pressBefore) * 3, "substitution",
                    Fmt.S($"Since {name} came on ({subMinute}'), {Fmt.Possessive(club.ShortName)} share of pressing actions is {Fmt.Pct(pressAfter)}%, up from {Fmt.Pct(pressBefore)}%."),
                    Fmt.S($"The change worked. Since {name} came on, {club.Name} are pressing higher and winning the ball back sooner.")));
            }
        }

        if (press >= 0.58)
        {
            candidates.Add((press + Math.Max(0, press - pressEarlier), "pressure",
                Fmt.S($"{club.ShortName} have {Fmt.Pct(press)}% of pressing actions in the last 10 minutes (was {Fmt.Pct(pressEarlier)}%)."),
                Fmt.S($"{club.Name} are squeezing the game, hunting the ball high up the pitch.")));
        }

        if (passShare >= 0.58)
        {
            candidates.Add((passShare, "territory",
                Fmt.S($"{club.ShortName} have {Fmt.Pct(passShare)}% of the passes in the last 10 minutes."),
                Fmt.S($"{club.Name} have the ball and are pinning their opponents back.")));
        }

        var shots = recent.For(gainer).Shots;
        if (shots >= 3)
        {
            facts["shots"] = shots;
            facts["shotsOnTarget"] = recent.For(gainer).ShotsOnTarget;
            candidates.Add((0.5 + 0.1 * shots, "shots",
                Fmt.S($"{shots} {club.ShortName} shots in 10 minutes, {recent.For(gainer).ShotsOnTarget} on target."),
                Fmt.S($"{club.Name} are peppering the goal: {shots} shots in 10 minutes.")));
        }

        var margin = ctx.Snapshot.For(gainer).Goals - ctx.Snapshot.For(gainer == Side.Home ? Side.Away : Side.Home).Goals;
        if (margin > 0 && ctx.Event.Minute >= 60)
        {
            var remaining = Math.Max(0, 90 - ctx.Event.Minute);
            facts["minutesLeft"] = remaining;
            candidates.Add((0.45, "clock",
                Fmt.S($"Game management: leading with {remaining} minutes left, every minute that passes protects the lead."),
                Fmt.S($"The clock is on their side. With {remaining} minutes left, every minute that passes protects the lead.")));
        }

        // Always available: the model's own reading of recent share of play.
        candidates.Add((0.3, "momentum",
            Fmt.S($"Recent share of play: {Fmt.Pct(passShare)}% of passes and {Fmt.Pct(press)}% of pressing actions in the last 10 minutes."),
            Fmt.S($"{club.Name} are growing into this game.")));

        var best = candidates.OrderByDescending(c => c.Score).First();
        var analyst = new TextVariant(
            Fmt.S($"Swing: {club.ShortName} {Fmt.Pct(wpFrom)}% to {Fmt.Pct(wpTo)}%"),
            best.Analyst);
        var casual = new TextVariant(
            Fmt.S($"{club.Name} on top"),
            Fmt.S($"{best.Casual} Their chance of winning: {Fmt.Pct(wpTo)}% (was {Fmt.Pct(wpFrom)}%)."));

        return InsightFactory.Create(ctx, Name, InsightKind.KeyMoment, 4, analyst, casual, facts,
            gainer, metric: "winProbability", tags: new Dictionary<string, string> { ["cause"] = best.Cause }, suffix: "swing");
    }

    private Insight? PressureShift(AgentContext ctx)
    {
        var now = ctx.Clock;
        var last = ctx.Window(PressureCheckSeconds);
        var prior = ctx.Window(now - 2 * PressureCheckSeconds, now - PressureCheckSeconds);
        if (last.Home.Pressure + last.Away.Pressure < MinPressureEvents || prior.Home.Pressure + prior.Away.Pressure < MinPressureEvents)
        {
            return null;
        }

        var delta = last.HomePressureShare - prior.HomePressureShare;
        var side = delta > 0 ? Side.Home : Side.Away;
        var nowShare = last.PressureShare(side);
        var thenShare = prior.PressureShare(side);
        if (Math.Abs(delta) < PressureShiftThreshold || nowShare < PressureShareToReport)
        {
            return null;
        }

        var club = ctx.ClubFor(side);
        return InsightFactory.Create(ctx, Name, InsightKind.KeyMoment, 3,
            new TextVariant(
                Fmt.S($"Pressure shift: {club.ShortName}"),
                Fmt.S($"Share of pressing actions {Fmt.Pct(thenShare)}% to {Fmt.Pct(nowShare)}% over the last two 5-minute windows.")),
            new TextVariant(
                Fmt.S($"{club.Name} turn up the heat"),
                Fmt.S($"They're closing down much faster now: {Fmt.Pct(nowShare)}% of the pressing in the last 5 minutes.")),
            new Dictionary<string, double> { ["pressBefore"] = Math.Round(thenShare, 2), ["pressAfter"] = Math.Round(nowShare, 2), ["windowMinutes"] = 5, ["windows"] = 2 },
            side, metric: "pressure", tags: new Dictionary<string, string> { ["cause"] = "pressure-shift" }, suffix: "pressure");
    }

    private sealed class MatchMemory
    {
        public OutcomeProbabilities? LastWp { get; set; }
        public OutcomeProbabilities? Announced { get; set; }
        public int LastSwingClock { get; set; } = -1000;
        public int LastWpMinute { get; set; } = -1;
        public int LastPressureCheck { get; set; }
        public List<(int Clock, Side Team, string? PlayerId)> Substitutions { get; } = [];
    }
}
