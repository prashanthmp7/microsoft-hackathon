using System.Collections.Concurrent;
using System.Globalization;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Data;

/// <summary>
/// Free-to-play prediction ledger: fan picks, micro-question answers and scores. No money, no odds.
/// Agents never write here. The referee agent publishes question/resolution insights and the ledger,
/// a platform service, applies them.
/// </summary>
public sealed class PredictionLedger : IFanPredictionReadModel, IFanPredictionWriter
{
    public const int OutcomePoints = 3;
    public const int ExactScoreBonus = 2;
    public const int MicroPoints = 1;
    private const int MaxFansPerMatch = 10_000;

    private readonly IMatchReadModel _matches;
    private readonly ConcurrentDictionary<string, MatchLedger> _ledgers = new(StringComparer.Ordinal);

    public PredictionLedger(IMatchReadModel matches, InsightStore insights)
    {
        _matches = matches;
        insights.Published += OnInsight;
    }

    public FanPick SubmitPick(FanPick pick)
    {
        if (_matches.GetStatus(pick.MatchId) != MatchStatus.Scheduled)
        {
            throw new InvalidOperationException("Predictions close at kick-off.");
        }

        var ledger = Ledger(pick.MatchId);
        lock (ledger)
        {
            if (ledger.Fans.Count >= MaxFansPerMatch && !ledger.Fans.ContainsKey(pick.FanId))
            {
                throw new InvalidOperationException("This match has reached its prediction limit.");
            }

            if (ledger.Picks.ContainsKey(pick.FanId))
            {
                throw new InvalidOperationException("You have already made a prediction for this match.");
            }

            ledger.Picks[pick.FanId] = pick;
            ledger.Fans.TryAdd(pick.FanId, new FanScore(pick.Nickname));
            return pick;
        }
    }

    public bool SubmitMicroAnswer(string matchId, string questionId, string fanId, string nickname, bool answerYes)
    {
        var ledger = Ledger(matchId);
        var clock = _matches.GetSnapshot(matchId)?.ClockSeconds ?? 0;
        lock (ledger)
        {
            if (ledger.Open is not { } question || question.Id != questionId || clock >= question.WindowStartClock)
            {
                return false;
            }

            if (ledger.Fans.Count >= MaxFansPerMatch && !ledger.Fans.ContainsKey(fanId))
            {
                return false;
            }

            ledger.Fans.TryAdd(fanId, new FanScore(nickname));
            return ledger.Answers.TryAdd((questionId, fanId), answerYes);
        }
    }

    public CrowdView GetCrowd(string matchId)
    {
        var ledger = Ledger(matchId);
        lock (ledger)
        {
            var picks = ledger.Picks.Values.ToList();
            if (picks.Count == 0)
            {
                return new CrowdView(0, 0, 0, 0);
            }

            double Share(MatchOutcome o) => Math.Round((double)picks.Count(p => p.Prediction.Outcome == o) / picks.Count, 3);
            return new CrowdView(picks.Count, Share(MatchOutcome.Home), Share(MatchOutcome.Draw), Share(MatchOutcome.Away));
        }
    }

    public MicroQuestion? GetOpenQuestion(string matchId)
    {
        var ledger = Ledger(matchId);
        lock (ledger)
        {
            return ledger.Open;
        }
    }

    public Leaderboard GetLeaderboard(string matchId)
    {
        var ledger = Ledger(matchId);
        lock (ledger)
        {
            var fans = ledger.Fans
                .Select(kv => new LeaderboardEntry(kv.Key, kv.Value.Nickname, kv.Value.Points, kv.Value.Correct, kv.Value.Answered))
                .OrderByDescending(f => f.Points).ThenByDescending(f => f.Correct).ThenBy(f => f.Nickname, StringComparer.Ordinal)
                .Take(50)
                .ToList();
            return new Leaderboard(matchId, fans,
                ledger.Model.ToScore("Model"), ledger.Pundit.ToScore("AI pundit"));
        }
    }

    private void OnInsight(Insight insight)
    {
        if (insight.Kind is not (InsightKind.PredictionOpened or InsightKind.PredictionResolved))
        {
            return;
        }

        var ledger = Ledger(insight.MatchId);
        lock (ledger)
        {
            if (insight.Kind == InsightKind.PredictionOpened)
            {
                ledger.Open = ToQuestion(insight);
                return;
            }

            if (insight.Tags.GetValueOrDefault("question") == "prematch")
            {
                ResolvePreMatch(ledger, insight);
            }
            else
            {
                ResolveMicro(ledger, insight);
            }
        }
    }

    private static void ResolvePreMatch(MatchLedger ledger, Insight insight)
    {
        if (!Enum.TryParse<MatchOutcome>(insight.Tags.GetValueOrDefault("outcome"), out var actual))
        {
            return;
        }

        var homeGoals = (int)insight.Facts.GetValueOrDefault("homeGoals");
        var awayGoals = (int)insight.Facts.GetValueOrDefault("awayGoals");
        foreach (var pick in ledger.Picks.Values)
        {
            var score = ledger.Fans[pick.FanId];
            score.Answered++;
            if (pick.Prediction.Outcome == actual)
            {
                score.Correct++;
                score.Points += OutcomePoints;
                if (pick.Prediction.HomeGoals == homeGoals && pick.Prediction.AwayGoals == awayGoals)
                {
                    score.Points += ExactScoreBonus;
                }
            }
        }

        ledger.Model.Apply(insight.Facts.GetValueOrDefault("modelCorrect") > 0, insight.Facts.GetValueOrDefault("modelExact") > 0);
        ledger.Pundit.Apply(insight.Facts.GetValueOrDefault("punditCorrect") > 0, insight.Facts.GetValueOrDefault("punditExact") > 0);
    }

    private static void ResolveMicro(MatchLedger ledger, Insight insight)
    {
        var questionId = insight.Tags.GetValueOrDefault("questionId");
        if (questionId is null || !ledger.Resolved.Add(questionId))
        {
            return;
        }

        var outcomeYes = insight.Tags.GetValueOrDefault("outcome") == "yes";
        foreach (var ((qid, fanId), answerYes) in ledger.Answers)
        {
            if (qid != questionId || !ledger.Fans.TryGetValue(fanId, out var score))
            {
                continue;
            }

            score.Answered++;
            if (answerYes == outcomeYes)
            {
                score.Correct++;
                score.Points += MicroPoints;
            }
        }

        ledger.Model.ApplyMicro(insight.Facts.GetValueOrDefault("modelCorrect") > 0);
        ledger.Pundit.ApplyMicro(insight.Facts.GetValueOrDefault("punditCorrect") > 0);
        if (ledger.Open?.Id == questionId)
        {
            ledger.Open = null;
        }
    }

    private static MicroQuestion ToQuestion(Insight insight)
    {
        int Int(string key) => int.Parse(insight.Tags.GetValueOrDefault(key, "0"), CultureInfo.InvariantCulture);
        return new MicroQuestion(
            insight.Tags.GetValueOrDefault("questionId", insight.Id),
            insight.MatchId,
            Enum.Parse<MicroQuestionType>(insight.Tags.GetValueOrDefault("type", nameof(MicroQuestionType.ShotOnTargetNext5))),
            insight.Casual.Body,
            insight.ClockSeconds,
            Int("windowStart"),
            Int("windowEnd"),
            insight.Facts.GetValueOrDefault("modelProbabilityYes"),
            insight.Facts.GetValueOrDefault("punditSaysYes") > 0);
    }

    private MatchLedger Ledger(string matchId) => _ledgers.GetOrAdd(matchId, _ => new MatchLedger());

    private sealed class MatchLedger
    {
        public Dictionary<string, FanPick> Picks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, FanScore> Fans { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string QuestionId, string FanId), bool> Answers { get; } = [];
        public HashSet<string> Resolved { get; } = new(StringComparer.Ordinal);
        public SourceAccumulator Model { get; } = new();
        public SourceAccumulator Pundit { get; } = new();
        public MicroQuestion? Open { get; set; }
    }

    private sealed class FanScore(string nickname)
    {
        public string Nickname { get; } = nickname;
        public int Points { get; set; }
        public int Correct { get; set; }
        public int Answered { get; set; }
    }

    private sealed class SourceAccumulator
    {
        private int _points;
        private int _correct;
        private int _answered;

        public void Apply(bool correct, bool exact)
        {
            _answered++;
            if (correct)
            {
                _correct++;
                _points += OutcomePoints + (exact ? ExactScoreBonus : 0);
            }
        }

        public void ApplyMicro(bool correct)
        {
            _answered++;
            if (correct)
            {
                _correct++;
                _points += MicroPoints;
            }
        }

        public SourceScore ToScore(string name) => new(name, _points, _correct, _answered);
    }
}
