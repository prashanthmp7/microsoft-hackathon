namespace SecondHalfMind.Domain;

/// <summary>
/// Marks an interface that can change source data. Only platform services may depend on these;
/// an architecture test fails the build if any agent type takes one as a dependency.
/// </summary>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class DataWriteCapabilityAttribute : Attribute;

// ---------------------------------------------------------------------------
// Read-only views. These are the ONLY data interfaces agents receive.
// ---------------------------------------------------------------------------

public interface ILeagueReadModel
{
    IReadOnlyList<Club> Clubs { get; }
    Club? GetClub(string clubId);
    Player? GetPlayer(string playerId);
    LeagueHistory History { get; }
    CalibrationProfile Calibration { get; }
}

public interface IMatchReadModel
{
    MatchInfo? GetMatch(string matchId);
    IReadOnlyList<MatchInfo> ListMatches();
    MatchStatus GetStatus(string matchId);
    MatchSnapshot? GetSnapshot(string matchId);
    IReadOnlyList<MatchEvent> GetEvents(string matchId, long afterSequence = 0, int max = 10_000);
}

public interface IInsightReadModel
{
    IReadOnlyList<Insight> GetInsights(string matchId, int skip = 0, int max = 500);
}

public interface IFanPredictionReadModel
{
    CrowdView GetCrowd(string matchId);
    Leaderboard GetLeaderboard(string matchId);
    MicroQuestion? GetOpenQuestion(string matchId);
}

/// <summary>Read-only stream of newly ingested events, consumed by the agent orchestrator.</summary>
public interface IMatchEventFeed
{
    IAsyncEnumerable<MatchEvent> ReadAllAsync(CancellationToken cancellationToken);
}

// ---------------------------------------------------------------------------
// Agent output channel. Append-only and physically separate from match data.
// ---------------------------------------------------------------------------

public interface IInsightSink
{
    void Publish(Insight insight);
}

public sealed record AuditEntry(DateTimeOffset At, string Actor, string Action, string? MatchId, string Outcome, string Detail)
{
    /// <summary>Hash of the previous entry; links the trail so tampering is detectable.</summary>
    public string? PreviousHash { get; init; }

    /// <summary>SHA-256 over this entry's fields and <see cref="PreviousHash"/>.</summary>
    public string? Hash { get; init; }
}

public interface IAuditLog
{
    void Record(string actor, string action, string? matchId, string outcome, string detail);
    IReadOnlyList<AuditEntry> Recent(string? matchId, int max);
}

// ---------------------------------------------------------------------------
// Write capabilities. Platform only. Note there is no Update, Delete or Remove anywhere:
// source data is append-only by construction.
// ---------------------------------------------------------------------------

[DataWriteCapability]
public interface IMatchEventWriter
{
    void Append(MatchEvent matchEvent);
}

[DataWriteCapability]
public interface IMatchRegistryWriter
{
    MatchInfo Register(string homeClubId, string awayClubId, int seed);
    void MarkLive(string matchId);
    void MarkFinished(string matchId);
}

[DataWriteCapability]
public interface IFanPredictionWriter
{
    FanPick SubmitPick(FanPick pick);
    bool SubmitMicroAnswer(string matchId, string questionId, string fanId, string nickname, bool answerYes);
}
