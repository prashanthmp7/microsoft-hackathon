namespace SecondHalfMind.Domain;

/// <summary>
/// Picks the moments worth replaying from a match's published insights.
/// Pure and deterministic so studios, streaming clients and tests all get the same reel
/// from the same insights; it reads insights only and never creates new text or numbers.
/// </summary>
public static class HighlightReel
{
    public const int DefaultCount = 8;
    public const int MaxCount = 12;

    private static readonly HashSet<InsightKind> EligibleKinds =
    [
        InsightKind.KeyMoment,
        InsightKind.PlayerAlert,
        InsightKind.Milestone,
        InsightKind.Stat,
    ];

    /// <summary>
    /// Returns eligible insights ranked best first. Callers take as many as they need
    /// (some may be dropped by personalisation) and then order them by clock.
    /// </summary>
    public static IReadOnlyList<Insight> Rank(IEnumerable<Insight> insights)
    {
        var seenSources = new HashSet<long>();
        var ranked = new List<Insight>();
        foreach (var insight in insights
                     .Where(IsEligible)
                     .OrderByDescending(Score)
                     .ThenBy(i => i.ClockSeconds)
                     .ThenBy(i => i.Id, StringComparer.Ordinal))
        {
            // One highlight per underlying event: a goal should not appear twice.
            if (seenSources.Add(insight.SourceSequence))
            {
                ranked.Add(insight);
            }
        }

        return ranked;
    }

    /// <summary>Takes the top <paramref name="count"/> ranked insights and returns them in match order.</summary>
    public static IReadOnlyList<Insight> Select(IEnumerable<Insight> insights, int count = DefaultCount) =>
        Chronological(Rank(insights).Take(Math.Clamp(count, 1, MaxCount)));

    public static IReadOnlyList<Insight> Chronological(IEnumerable<Insight> selected) =>
        selected.OrderBy(i => i.ClockSeconds).ThenBy(i => i.SourceSequence).ToList();

    private static bool IsEligible(Insight i) =>
        EligibleKinds.Contains(i.Kind) && (i.Kind == InsightKind.KeyMoment || i.Priority >= 3);

    private static int Score(Insight i) => (i.Priority * 10) + (i.Kind == InsightKind.KeyMoment ? 5 : 0);
}
