namespace SecondHalfMind.Domain;

public enum MatchStatus { Scheduled, Live, Finished }

public enum MatchPhase { PreMatch, FirstHalf, HalfTime, SecondHalf, FullTime }

public sealed record MatchInfo(string Id, string HomeClubId, string AwayClubId, int Seed, DateTimeOffset CreatedAt);

public enum MatchEventType
{
    KickOff,
    Pass,
    Shot,
    Goal,
    Save,
    Tackle,
    Interception,
    PossessionChange,
    Pressure,
    Foul,
    Corner,
    YellowCard,
    RedCard,
    Substitution,
    Sprint,
    HalfTime,
    FullTime,
}

/// <summary>
/// One synthetic match event. Pitch coordinates are metres on a 105 x 68 pitch with the home side
/// attacking towards X = 105. ClockSeconds runs 0..5400 (no stoppage time is simulated).
/// </summary>
public sealed record MatchEvent
{
    public required string Id { get; init; }
    public required string MatchId { get; init; }
    public required long Sequence { get; init; }
    public required int ClockSeconds { get; init; }
    public required MatchEventType Type { get; init; }
    public required Side Team { get; init; }
    public string? PlayerId { get; init; }
    public string? TargetPlayerId { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double? EndX { get; init; }
    public double? EndY { get; init; }
    public bool? Success { get; init; }
    public bool UnderPressure { get; init; }
    public double? SpeedKmh { get; init; }
    public double? ExpectedGoals { get; init; }
    public bool? OnTarget { get; init; }

    public int Minute => Type == MatchEventType.HalfTime ? 45 : ToMinute(ClockSeconds);

    public static int ToMinute(int clockSeconds) => Math.Min(90, clockSeconds / 60 + 1);
}

public sealed record GoalRecord(int Minute, Side Team, string PlayerId);

public sealed record TeamStats(
    int Goals,
    int Shots,
    int ShotsOnTarget,
    double ExpectedGoals,
    int PassesAttempted,
    int PassesCompleted,
    int Tackles,
    int Interceptions,
    int Corners,
    int Fouls,
    int YellowCards,
    int RedCards,
    int PressureEvents,
    int Substitutions,
    double PossessionSeconds,
    double MaxShotSpeedKmh)
{
    public static TeamStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public double PassAccuracy => PassesAttempted == 0 ? 0 : (double)PassesCompleted / PassesAttempted;
}

public sealed record PlayerLive(
    string PlayerId,
    Side Team,
    bool OnPitch,
    int Passes,
    int PassesCompleted,
    int Shots,
    int Goals,
    int Tackles,
    double DistanceKm,
    double TopSpeedKmh);

/// <summary>Immutable projection of a match at a point in time. Produced by the platform, read by agents.</summary>
public sealed record MatchSnapshot(
    string MatchId,
    long LastSequence,
    int ClockSeconds,
    MatchPhase Phase,
    TeamStats Home,
    TeamStats Away,
    IReadOnlyList<GoalRecord> Goals,
    IReadOnlyDictionary<string, PlayerLive> Players)
{
    public int Minute => Phase switch
    {
        MatchPhase.PreMatch => 0,
        MatchPhase.HalfTime => 45,
        _ => MatchEvent.ToMinute(ClockSeconds),
    };

    public double HomePossessionShare
    {
        get
        {
            var total = Home.PossessionSeconds + Away.PossessionSeconds;
            return total <= 0 ? 0.5 : Home.PossessionSeconds / total;
        }
    }

    public TeamStats For(Side side) => side == Side.Home ? Home : Away;
}
