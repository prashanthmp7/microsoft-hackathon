using System.Collections.Concurrent;
using SecondHalfMind.Agents.Analytics;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents;

/// <summary>
/// Interpret stage: turns raw events into football metrics. Pass difficulty, shot speed and xG,
/// sprint and distance threshold alerts, milestones, and a control-vs-chaos reading every five minutes.
/// </summary>
public sealed class StatsAgent : IMatchAgent
{
    public const double DifficultPassThreshold = 8.2;
    public const double PlayerActionPassThreshold = 6.5;
    public const double SprintAlertKmh = 34.0;
    public const double RocketShotKmh = 110;
    private const int ControlWindowSeconds = 300;
    private const double InControlPassShare = 0.6;
    private static readonly double[] DistanceMilestonesKm = [5, 10];

    private readonly ConcurrentDictionary<string, MatchMemory> _memory = new(StringComparer.Ordinal);

    public string Name => "stats";

    public AgentStage Stage => AgentStage.Analysis;

    public IEnumerable<Insight> OnEvent(AgentContext ctx)
    {
        var memory = _memory.GetOrAdd(ctx.Match.Id, _ => new MatchMemory());
        var e = ctx.Event;
        var output = new List<Insight>();

        switch (e.Type)
        {
            case MatchEventType.Pass when e.Success == true:
                AddPass(ctx, output);
                AddPassMilestone(ctx, memory, output);
                break;
            case MatchEventType.Shot:
                AddShot(ctx, memory, output);
                break;
            case MatchEventType.Sprint:
                AddSprint(ctx, memory, output);
                break;
            case MatchEventType.Tackle or MatchEventType.Interception:
                output.Add(PlayerAction(ctx, "won the ball", "Ball won", Fmt.S($"{ctx.PlayerName(e.PlayerId)} wins it back for {ctx.ClubFor(e.Team).ShortName}."), []));
                break;
        }

        AddDistanceAlerts(ctx, memory, output);

        if (ctx.Snapshot.Phase is MatchPhase.FirstHalf or MatchPhase.SecondHalf
            && e.ClockSeconds - memory.LastControlClock >= ControlWindowSeconds)
        {
            memory.LastControlClock = e.ClockSeconds - e.ClockSeconds % ControlWindowSeconds;
            AddControl(ctx, memory, output);
        }

        return output;
    }

    private void AddPass(AgentContext ctx, List<Insight> output)
    {
        var e = ctx.Event;
        var difficulty = PassDifficulty.Rate(e);
        var length = PassDifficulty.Length(e);
        var progression = PassDifficulty.Progression(e);
        var passer = ctx.PlayerName(e.PlayerId);
        var receiver = ctx.PlayerName(e.TargetPlayerId);
        var facts = new Dictionary<string, double>
        {
            ["difficulty"] = difficulty,
            ["scaleMax"] = 10,
            ["lengthM"] = Math.Round(length),
            ["progressionM"] = Math.Round(progression),
            ["ballSpeedKmh"] = Math.Round(e.SpeedKmh ?? 0),
        };

        if (difficulty >= DifficultPassThreshold)
        {
            var pressure = e.UnderPressure ? " under pressure" : string.Empty;
            output.Add(InsightFactory.Create(ctx, Name, InsightKind.Stat, 2,
                new TextVariant(
                    Fmt.S($"Pass difficulty {Fmt.N1(difficulty)}/10"),
                    Fmt.S($"{passer} to {receiver}: {Fmt.N0(length)} m{pressure}, {Fmt.N0(progression)} m forward at {Fmt.N0(e.SpeedKmh ?? 0)} km/h.")),
                new TextVariant(
                    Fmt.S($"What a pass from {passer}"),
                    Fmt.S($"A {Fmt.N0(length)} m ball{pressure} that finds {receiver} and cuts through the lines.")),
                facts, e.Team, e.PlayerId, "passDifficulty"));
        }
        else if (difficulty >= PlayerActionPassThreshold)
        {
            output.Add(PlayerAction(ctx, "pass", "Progressive pass",
                Fmt.S($"{passer} plays it {Fmt.N0(progression)} m forward to {receiver} (difficulty {Fmt.N1(difficulty)})."), facts));
        }
    }

    private void AddPassMilestone(AgentContext ctx, MatchMemory memory, List<Insight> output)
    {
        var e = ctx.Event;
        if (e.PlayerId is null || !ctx.Snapshot.Players.TryGetValue(e.PlayerId, out var live))
        {
            return;
        }

        foreach (var milestone in new[] { 40, 60 })
        {
            if (live.PassesCompleted == milestone && memory.Milestones.Add($"{e.PlayerId}-passes-{milestone}"))
            {
                var name = ctx.PlayerName(e.PlayerId);
                var accuracy = live.Passes == 0 ? 0 : (double)live.PassesCompleted / live.Passes;
                output.Add(InsightFactory.Create(ctx, Name, InsightKind.Milestone, 2,
                    new TextVariant(Fmt.S($"{name}: {milestone} completed passes"), Fmt.S($"{Fmt.Pct(accuracy)}% accuracy from {live.Passes} attempts.")),
                    new TextVariant(Fmt.S($"{name} is running the show"), Fmt.S($"{milestone} passes completed already.")),
                    new Dictionary<string, double> { ["milestone"] = milestone, ["accuracy"] = Math.Round(accuracy, 3), ["attempts"] = live.Passes },
                    e.Team, e.PlayerId, "passing", suffix: milestone.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
    }

    private void AddShot(AgentContext ctx, MatchMemory memory, List<Insight> output)
    {
        var e = ctx.Event;
        var speed = e.SpeedKmh ?? 0;
        var xg = e.ExpectedGoals ?? 0;
        var distance = PassDifficulty.DistanceToGoal(e);
        var shooter = ctx.PlayerName(e.PlayerId);
        var club = ctx.ClubFor(e.Team);
        var rocket = speed >= RocketShotKmh;
        var priority = rocket || xg >= 0.3 ? 3 : 2;
        var outcome = e.OnTarget == true ? "on target" : "off target";

        output.Add(InsightFactory.Create(ctx, Name, InsightKind.Stat, priority,
            new TextVariant(
                Fmt.S($"Shot {Fmt.N0(speed)} km/h, xG {Fmt.N2(xg)}"),
                Fmt.S($"{shooter} ({club.ShortName}) from {Fmt.N0(distance)} m, {outcome}.")),
            new TextVariant(
                rocket ? Fmt.S($"Rocket from {shooter}!") : xg >= 0.3 ? Fmt.S($"Big chance for {shooter}") : Fmt.S($"{shooter} tries his luck"),
                Fmt.S($"{Fmt.N0(speed)} km/h from {Fmt.N0(distance)} m. A chance like that goes in about {Fmt.Pct(xg)}% of the time.")),
            new Dictionary<string, double> { ["speedKmh"] = Math.Round(speed), ["xg"] = Math.Round(xg, 2), ["distanceM"] = Math.Round(distance) },
            e.Team, e.PlayerId, "shotSpeed"));

        var shots = ctx.Snapshot.For(e.Team).Shots;
        if (shots is 10 or 15 && memory.Milestones.Add($"{e.Team}-shots-{shots}"))
        {
            output.Add(InsightFactory.Create(ctx, Name, InsightKind.Milestone, 2,
                new TextVariant(Fmt.S($"{club.ShortName}: {shots} shots"), Fmt.S($"{ctx.Snapshot.For(e.Team).ShotsOnTarget} on target, xG {Fmt.N2(ctx.Snapshot.For(e.Team).ExpectedGoals)}.")),
                new TextVariant(Fmt.S($"{club.Name} keep shooting"), Fmt.S($"That's {shots} attempts so far.")),
                new Dictionary<string, double> { ["shots"] = shots, ["onTarget"] = ctx.Snapshot.For(e.Team).ShotsOnTarget, ["xg"] = ctx.Snapshot.For(e.Team).ExpectedGoals },
                e.Team, metric: "shots", suffix: "shots"));
        }

        output.Add(PlayerAction(ctx, "shot", "Shot", Fmt.S($"{shooter} shoots: {Fmt.N0(speed)} km/h, xG {Fmt.N2(xg)}."),
            new Dictionary<string, double> { ["speedKmh"] = Math.Round(speed), ["xg"] = Math.Round(xg, 2) }));
    }

    private void AddSprint(AgentContext ctx, MatchMemory memory, List<Insight> output)
    {
        var e = ctx.Event;
        var speed = e.SpeedKmh ?? 0;
        if (e.PlayerId is null || speed < SprintAlertKmh)
        {
            return;
        }

        var best = memory.SpeedAlerts.GetValueOrDefault(e.PlayerId);
        if (speed <= best)
        {
            return;
        }

        memory.SpeedAlerts[e.PlayerId] = speed;
        var name = ctx.PlayerName(e.PlayerId);
        output.Add(InsightFactory.Create(ctx, Name, InsightKind.PlayerAlert, 2,
            new TextVariant(Fmt.S($"{name}: {Fmt.N1(speed)} km/h"), Fmt.S($"Sprint above the {Fmt.N0(SprintAlertKmh)} km/h alert threshold.")),
            new TextVariant(Fmt.S($"{name} is flying"), Fmt.S($"Clocked at {Fmt.N1(speed)} km/h.")),
            new Dictionary<string, double> { ["speedKmh"] = Math.Round(speed, 1), ["thresholdKmh"] = SprintAlertKmh },
            e.Team, e.PlayerId, "speed"));
    }

    private void AddDistanceAlerts(AgentContext ctx, MatchMemory memory, List<Insight> output)
    {
        foreach (var live in ctx.Snapshot.Players.Values)
        {
            if (!live.OnPitch)
            {
                continue;
            }

            foreach (var km in DistanceMilestonesKm)
            {
                if (live.DistanceKm >= km && memory.Milestones.Add($"{live.PlayerId}-km-{km}"))
                {
                    var name = ctx.PlayerName(live.PlayerId);
                    output.Add(InsightFactory.Create(ctx, Name, InsightKind.PlayerAlert, km >= 10 ? 2 : 1,
                        new TextVariant(Fmt.S($"{name}: {Fmt.N0(km)} km covered"), Fmt.S($"Distance threshold reached in minute {ctx.Event.Minute}.")),
                        new TextVariant(Fmt.S($"{name} has run {Fmt.N0(km)} km"), "Covering every blade of grass."),
                        new Dictionary<string, double> { ["distanceKm"] = km },
                        live.Team, live.PlayerId, "distance", suffix: Fmt.S($"{live.PlayerId}-{km}")));
                }
            }
        }
    }

    private void AddControl(AgentContext ctx, MatchMemory memory, List<Insight> output)
    {
        var window = ctx.Window(ControlWindowSeconds);
        if (window.Home.Passes + window.Away.Passes < 10)
        {
            return;
        }

        var controlHome = window.ControlIndex(Side.Home);
        var controlAway = window.ControlIndex(Side.Away);
        var chaos = window.ChaosIndex;
        var tempo = Math.Round(window.Tempo, 1);
        var tempoChange = memory.LastTempo is { } last && last > 0 ? (tempo - last) / last : 0;
        memory.LastTempo = tempo;

        var leader = controlHome >= controlAway ? Side.Home : Side.Away;
        var leaderClub = ctx.ClubFor(leader);
        var share = window.PassShare(leader);
        var tpm = Math.Round(window.TurnoversPerMinute, 1);

        var facts = new Dictionary<string, double>
        {
            ["controlHome"] = controlHome,
            ["controlAway"] = controlAway,
            ["chaos"] = chaos,
            ["windowMinutes"] = 5,
            ["leaderPassShare"] = Math.Round(share, 2),
            ["completionHome"] = Math.Round(window.Home.Completion, 2),
            ["completionAway"] = Math.Round(window.Away.Completion, 2),
            ["turnoversPerMinute"] = tpm,
            ["tempo"] = tempo,
            ["tempoChange"] = Math.Round(tempoChange, 2),
        };

        var rhythm = Math.Abs(tempoChange) >= 0.3
            ? Fmt.S($" Tempo {(tempoChange > 0 ? "up" : "down")} {Fmt.Pct(Math.Abs(tempoChange))}% to {Fmt.N1(tempo)} passes a minute.")
            : string.Empty;

        var analyst = new TextVariant(
            Fmt.S($"Control {ctx.Home.ShortName} {controlHome} · {ctx.Away.ShortName} {controlAway} | Chaos {chaos}"),
            Fmt.S($"Last 5 min: completion {Fmt.Pct(window.Home.Completion)}% vs {Fmt.Pct(window.Away.Completion)}%, {Fmt.N1(tpm)} turnovers a minute.{rhythm}"));

        TextVariant casual;
        int priority;
        if (chaos >= 60)
        {
            casual = new TextVariant("End to end!", Fmt.S($"The ball is changing hands {Fmt.N1(tpm)} times a minute. Neither side can settle."));
            priority = 3;
        }
        else if (share >= InControlPassShare)
        {
            casual = new TextVariant(Fmt.S($"{leaderClub.Name} in control"), Fmt.S($"They've had {Fmt.Pct(share)}% of the passes in the last 5 minutes."));
            priority = 3;
        }
        else
        {
            casual = new TextVariant("Evenly poised", "Neither side has taken control in the last five minutes.");
            priority = 2;
        }

        output.Add(InsightFactory.Create(ctx, Name, InsightKind.Stat, priority, analyst, casual, facts,
            leader, metric: "control", suffix: "control"));
    }

    private Insight PlayerAction(AgentContext ctx, string action, string headline, string body, Dictionary<string, double> facts) =>
        InsightFactory.Create(ctx, Name, InsightKind.PlayerAlert, 1,
            new TextVariant(Fmt.S($"{headline}: {ctx.PlayerName(ctx.Event.PlayerId)}"), body),
            new TextVariant(Fmt.S($"{headline}: {ctx.PlayerName(ctx.Event.PlayerId)}"), body),
            new Dictionary<string, double>(facts),
            ctx.Event.Team, ctx.Event.PlayerId, "playerAction", suffix: action.Replace(' ', '-'));

    private sealed class MatchMemory
    {
        public HashSet<string> Milestones { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> SpeedAlerts { get; } = new(StringComparer.Ordinal);
        public int LastControlClock { get; set; }
        public double? LastTempo { get; set; }
    }
}
