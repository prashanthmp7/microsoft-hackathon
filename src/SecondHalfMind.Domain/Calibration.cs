namespace SecondHalfMind.Domain;

/// <summary>
/// League-level statistical priors. When computed by the calibration tool these come from real
/// historical English top-flight results, reduced to anonymous aggregates: no club names,
/// no player names and no individual match rows are kept.
/// </summary>
public sealed record CalibrationProfile
{
    public required string Source { get; init; }
    public required bool IsDefault { get; init; }
    public required IReadOnlyList<string> Seasons { get; init; }
    public required int MatchCount { get; init; }
    public required DateTimeOffset ComputedAt { get; init; }

    public required double HomeWinRate { get; init; }
    public required double DrawRate { get; init; }
    public required double AwayWinRate { get; init; }
    public required double MeanHomeGoals { get; init; }
    public required double MeanAwayGoals { get; init; }

    /// <summary>Standard deviation of log attack strength across clubs.</summary>
    public required double AttackLogStd { get; init; }

    /// <summary>Standard deviation of log defensive strength across clubs.</summary>
    public required double DefenseLogStd { get; init; }

    public required double ShotsPerTeamMean { get; init; }
    public required double ShotsOnTargetRate { get; init; }
    public required double GoalsPerShot { get; init; }
    public required double CornersPerTeamMean { get; init; }
    public required double YellowCardsPerTeamMean { get; init; }
    public required double RedCardsPerTeamMean { get; init; }

    public double HomeAdvantage => MeanAwayGoals > 0 ? MeanHomeGoals / MeanAwayGoals : 1.0;

    /// <summary>Approximate, typical top-flight figures used until the calibration tool has been run.</summary>
    public static CalibrationProfile Defaults { get; } = new()
    {
        Source = "default-priors (approximate; run the calibrate command to compute from historical results)",
        IsDefault = true,
        Seasons = Array.Empty<string>(),
        MatchCount = 0,
        ComputedAt = DateTimeOffset.UnixEpoch,
        HomeWinRate = 0.45,
        DrawRate = 0.23,
        AwayWinRate = 0.32,
        MeanHomeGoals = 1.62,
        MeanAwayGoals = 1.33,
        AttackLogStd = 0.27,
        DefenseLogStd = 0.24,
        ShotsPerTeamMean = 12.8,
        ShotsOnTargetRate = 0.34,
        GoalsPerShot = 0.11,
        CornersPerTeamMean = 5.1,
        YellowCardsPerTeamMean = 1.9,
        RedCardsPerTeamMean = 0.05,
    };
}
