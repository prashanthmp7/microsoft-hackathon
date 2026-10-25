using System.Collections.Concurrent;
using System.Globalization;
using SecondHalfMind.Agents.Analytics;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents;

/// <summary>
/// Opens free-to-play micro predictions during the match, has the model and the AI pundit answer
/// them, resolves them from the event stream and explains the result. Also settles the pre-match calls
/// at full time. It only publishes insights; the platform's ledger applies the scores.
/// </summary>
public sealed class PredictionRefereeAgent : IMatchAgent
{
    /// <summary>Time fans have to answer before the question window starts.</summary>
    public const int AnswerSeconds = 120;

    private static readonly (int OpenAt, MicroQuestionType Type)[] Schedule =
    [
        (300, MicroQuestionType.ShotOnTargetNext5),
        (900, MicroQuestionType.CornerNext5),
        (1500, MicroQuestionType.GoalNext10),
        (2100, MicroQuestionType.HomeMorePossessionNext5),
        (3000, MicroQuestionType.ShotOnTargetNext5),
        (3600, MicroQuestionType.GoalNext10),
        (4200, MicroQuestionType.CornerNext5),
        (4800, MicroQuestionType.ShotOnTargetNext5),
    ];

    private readonly ConcurrentDictionary<string, MatchMemory> _memory = new(StringComparer.Ordinal);

    public string Name => "referee";

    public AgentStage Stage => AgentStage.Interpretation;

    public IEnumerable<Insight> OnEvent(AgentContext ctx)
    {
        var memory = _memory.GetOrAdd(ctx.Match.Id, _ => new MatchMemory());
        var output = new List<Insight>();
        var clock = ctx.Clock;

        if (memory.Open is { } open && clock > open.WindowEndClock)
        {
            output.Add(Resolve(ctx, open));
            memory.Open = null;
        }

        if (memory.Open is null && memory.NextIndex < Schedule.Length && clock >= Schedule[memory.NextIndex].OpenAt
            && ctx.Snapshot.Phase is MatchPhase.FirstHalf or MatchPhase.SecondHalf)
        {
            var (_, type) = Schedule[memory.NextIndex];
            memory.NextIndex++;
            var question = Open(ctx, type);
            memory.Open = question.Question;
            output.Add(question.Insight);
        }

        if (ctx.Event.Type == MatchEventType.FullTime)
        {
            output.Add(SettlePreMatch(ctx));
        }

        return output;
    }

    private (MicroQuestion Question, Insight Insight) Open(AgentContext ctx, MicroQuestionType type)
    {
        var start = ctx.Clock + AnswerSeconds;
        var length = type == MicroQuestionType.GoalNext10 ? 600 : 300;
        var end = start + length;
        var calibration = ctx.League.Calibration;
        var recent = ctx.Window(600);
        var minutes = length / 60.0;

        // Model: Poisson rates from the calibrated league averages, tilted by recent tempo.
        var tempoFactor = Math.Clamp(recent.Tempo / 9.5, 0.7, 1.4);
        double modelYes;
        string text;
        switch (type)
        {
            case MicroQuestionType.ShotOnTargetNext5:
                var sotPerMinute = 2 * calibration.ShotsPerTeamMean * calibration.ShotsOnTargetRate / 90 * tempoFactor;
                modelYes = 1 - Math.Exp(-sotPerMinute * minutes);
                text = "Will there be a shot on target in the next 5 minutes?";
                break;
            case MicroQuestionType.CornerNext5:
                var cornersPerMinute = 2 * calibration.CornersPerTeamMean / 90 * tempoFactor;
                modelYes = 1 - Math.Exp(-cornersPerMinute * minutes);
                text = "Will there be a corner in the next 5 minutes?";
                break;
            case MicroQuestionType.GoalNext10:
                var goalsPerMinute = (ctx.Briefing.ExpectedHomeGoals + ctx.Briefing.ExpectedAwayGoals) / 90;
                modelYes = 1 - Math.Exp(-goalsPerMinute * minutes);
                text = "Will there be a goal in the next 10 minutes?";
                break;
            default:
                modelYes = 1 / (1 + Math.Exp(-8 * (recent.HomePassShare - 0.5)));
                text = $"Will {ctx.Home.Name} have more of the ball in the next 5 minutes?";
                break;
        }

        // Pundit: gut feel from what just happened.
        var punditYes = type switch
        {
            MicroQuestionType.ShotOnTargetNext5 => recent.Home.Shots + recent.Away.Shots >= 2,
            MicroQuestionType.CornerNext5 => recent.Home.Corners + recent.Away.Corners >= 1,
            MicroQuestionType.GoalNext10 => ctx.Snapshot.Home.Goals == ctx.Snapshot.Away.Goals && ctx.Event.Minute >= 60,
            _ => ctx.Snapshot.HomePossessionShare >= 0.5,
        };

        var id = Fmt.S($"{ctx.Match.Id}-q{ctx.Clock}");
        modelYes = Math.Round(modelYes, 3);
        var question = new MicroQuestion(id, ctx.Match.Id, type, text, ctx.Clock, start, end, modelYes, punditYes);
        var insight = InsightFactory.Create(ctx, Name, InsightKind.PredictionOpened, 4,
            new TextVariant("Prediction open", Fmt.S($"{text} Model: {Fmt.Pct(modelYes)}% yes. Pundit: {(punditYes ? "yes" : "no")}.")),
            new TextVariant("Call it!", text),
            new Dictionary<string, double>
            {
                ["modelProbabilityYes"] = modelYes,
                ["punditSaysYes"] = punditYes ? 1 : 0,
                ["answerSeconds"] = AnswerSeconds,
                ["windowMinutes"] = minutes,
            },
            metric: "prediction",
            tags: new Dictionary<string, string>
            {
                ["questionId"] = id,
                ["type"] = type.ToString(),
                ["windowStart"] = start.ToString(CultureInfo.InvariantCulture),
                ["windowEnd"] = end.ToString(CultureInfo.InvariantCulture),
            },
            suffix: "open");
        return (question, insight);
    }

    private Insight Resolve(AgentContext ctx, MicroQuestion q)
    {
        var w = WindowMetrics.Compute(ctx.Recent, q.WindowStartClock, q.WindowEndClock);
        var (yes, evidence, facts) = q.Type switch
        {
            MicroQuestionType.ShotOnTargetNext5 => (
                w.Home.ShotsOnTarget + w.Away.ShotsOnTarget > 0,
                Fmt.S($"{Fmt.Count(w.Home.ShotsOnTarget + w.Away.ShotsOnTarget, "shot")} on target in the window ({Fmt.Count(w.Home.Shots + w.Away.Shots, "shot")} in total)."),
                new Dictionary<string, double> { ["onTarget"] = w.Home.ShotsOnTarget + w.Away.ShotsOnTarget, ["shots"] = w.Home.Shots + w.Away.Shots }),
            MicroQuestionType.CornerNext5 => (
                w.Home.Corners + w.Away.Corners > 0,
                Fmt.S($"{Fmt.Count(w.Home.Corners + w.Away.Corners, "corner")} in the window."),
                new Dictionary<string, double> { ["corners"] = w.Home.Corners + w.Away.Corners }),
            MicroQuestionType.GoalNext10 => (
                w.Home.Goals + w.Away.Goals > 0,
                Fmt.S($"{Fmt.Count(w.Home.Goals + w.Away.Goals, "goal")} in the window from {Fmt.Count(w.Home.Shots + w.Away.Shots, "shot")}."),
                new Dictionary<string, double> { ["goals"] = w.Home.Goals + w.Away.Goals, ["shots"] = w.Home.Shots + w.Away.Shots }),
            _ => (
                w.HomePassShare > 0.5,
                Fmt.S($"{ctx.Home.ShortName} had {Fmt.Pct(w.HomePassShare)}% of the passes in the window."),
                new Dictionary<string, double> { ["homePassShare"] = Math.Round(w.HomePassShare, 2) }),
        };

        var modelCorrect = (q.ModelProbabilityYes >= 0.5) == yes;
        var punditCorrect = q.PunditSaysYes == yes;
        facts["modelProbabilityYes"] = q.ModelProbabilityYes;
        facts["modelCorrect"] = modelCorrect ? 1 : 0;
        facts["punditCorrect"] = punditCorrect ? 1 : 0;

        var answer = yes ? "Yes" : "No";
        string Verdict(bool correct) => correct ? "right" : "wrong";
        var analyst = new TextVariant(
            Fmt.S($"Resolved: {answer}"),
            Fmt.S($"{evidence} Model said {Fmt.Pct(q.ModelProbabilityYes)}% ({Verdict(modelCorrect)}), pundit said {(q.PunditSaysYes ? "yes" : "no")} ({Verdict(punditCorrect)})."));
        var casual = new TextVariant(
            Fmt.S($"The answer is {answer}!"),
            Fmt.S($"{evidence} The model was {Verdict(modelCorrect)}, the pundit was {Verdict(punditCorrect)}."));

        return InsightFactory.Create(ctx, Name, InsightKind.PredictionResolved, 4, analyst, casual, facts,
            metric: "prediction",
            tags: new Dictionary<string, string> { ["questionId"] = q.Id, ["outcome"] = yes ? "yes" : "no", ["question"] = "micro" },
            suffix: "resolve");
    }

    private Insight SettlePreMatch(AgentContext ctx)
    {
        var s = ctx.Snapshot;
        var actual = s.Home.Goals > s.Away.Goals ? MatchOutcome.Home : s.Home.Goals == s.Away.Goals ? MatchOutcome.Draw : MatchOutcome.Away;
        var model = ctx.Briefing.Model.Prediction;
        var pundit = ctx.Briefing.Pundit.Prediction;
        bool Exact(ScorePrediction p) => p.HomeGoals == s.Home.Goals && p.AwayGoals == s.Away.Goals;

        var facts = new Dictionary<string, double>
        {
            ["homeGoals"] = s.Home.Goals,
            ["awayGoals"] = s.Away.Goals,
            ["modelCorrect"] = model.Outcome == actual ? 1 : 0,
            ["modelExact"] = Exact(model) ? 1 : 0,
            ["punditCorrect"] = pundit.Outcome == actual ? 1 : 0,
            ["punditExact"] = Exact(pundit) ? 1 : 0,
            ["modelHome"] = model.HomeGoals,
            ["modelAway"] = model.AwayGoals,
            ["punditHome"] = pundit.HomeGoals,
            ["punditAway"] = pundit.AwayGoals,
        };

        string Call(string who, ScorePrediction p) =>
            Fmt.S($"{who} said {p.HomeGoals}-{p.AwayGoals} ({(p.Outcome == actual ? (Exact(p) ? "exact" : "right result") : "wrong")})");

        var why = (model.Outcome == actual, pundit.Outcome == actual) switch
        {
            (true, false) => "The numbers beat gut feel this time.",
            (false, true) => "Form and instinct beat the model this time.",
            (true, true) => "Model and pundit both read it right.",
            _ => "Nobody saw that coming, which is why we play the games.",
        };

        return InsightFactory.Create(ctx, Name, InsightKind.PredictionResolved, 5,
            new TextVariant("Who called it?", Fmt.S($"Final {s.Home.Goals}-{s.Away.Goals}. {Call("Model", model)}; {Call("pundit", pundit)}. {why}")),
            new TextVariant("Who called it?", Fmt.S($"{Call("The model", model)}. {Call("The pundit", pundit)}. {why}")),
            facts,
            metric: "prediction",
            tags: new Dictionary<string, string> { ["question"] = "prematch", ["outcome"] = actual.ToString() },
            suffix: "prematch");
    }

    private sealed class MatchMemory
    {
        public int NextIndex { get; set; }
        public MicroQuestion? Open { get; set; }
    }
}
