namespace SecondHalfMind.Domain;

public enum MatchOutcome { Home, Draw, Away }

public enum MicroQuestionType { ShotOnTargetNext5, CornerNext5, GoalNext10, HomeMorePossessionNext5 }

public sealed record ScorePrediction(MatchOutcome Outcome, int HomeGoals, int AwayGoals);

public sealed record OutcomeProbabilities(double Home, double Draw, double Away)
{
    public MatchOutcome MostLikely =>
        Home >= Draw && Home >= Away ? MatchOutcome.Home : Away >= Draw ? MatchOutcome.Away : MatchOutcome.Draw;
}

public sealed record PredictionFactor(string Name, string Description, double Value);

/// <summary>Pre-match call from the statistical model or the AI pundit, with the reasons behind it.</summary>
public sealed record PreMatchCall(
    string Source,
    ScorePrediction Prediction,
    OutcomeProbabilities? Probabilities,
    IReadOnlyList<PredictionFactor> Factors,
    string Rationale);

public sealed record Storyline(string Id, string Title, string Description, string WatchFor);

public sealed record PreMatchBriefing(
    string MatchId,
    PreMatchCall Model,
    PreMatchCall Pundit,
    IReadOnlyList<Storyline> Storylines,
    double ExpectedHomeGoals,
    double ExpectedAwayGoals);

/// <summary>A fan's pre-match pick. The nickname is validated, display-only and never sent to a language model.</summary>
public sealed record FanPick(string FanId, string Nickname, string MatchId, ScorePrediction Prediction, DateTimeOffset SubmittedAt);

public sealed record MicroQuestion(
    string Id,
    string MatchId,
    MicroQuestionType Type,
    string Text,
    int OpenedAtClock,
    int WindowStartClock,
    int WindowEndClock,
    double ModelProbabilityYes,
    bool PunditSaysYes);

public sealed record CrowdView(int Picks, double HomeShare, double DrawShare, double AwayShare);

public sealed record LeaderboardEntry(string FanId, string Nickname, int Points, int Correct, int Answered);

public sealed record SourceScore(string Source, int Points, int Correct, int Answered);

public sealed record Leaderboard(string MatchId, IReadOnlyList<LeaderboardEntry> Fans, SourceScore Model, SourceScore Pundit);
