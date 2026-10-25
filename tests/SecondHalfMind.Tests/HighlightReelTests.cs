using SecondHalfMind.Api;
using SecondHalfMind.Domain;
using Xunit;

namespace SecondHalfMind.Tests;

public class HighlightReelTests
{
    private static Insight Make(string id, InsightKind kind, int priority, int clock, long source) => new()
    {
        Id = id,
        MatchId = "m1",
        SourceSequence = source,
        ClockSeconds = clock,
        Kind = kind,
        Agent = "test",
        Priority = priority,
        Analyst = new TextVariant("A", "a"),
        Casual = new TextVariant("C", "c"),
        GeneratedBy = "template",
        CreatedAt = DateTimeOffset.UnixEpoch,
        Minute = clock / 60 + 1,
    };

    [Fact]
    public void Excludes_win_probability_predictions_recaps_and_low_priority_stats()
    {
        var reel = HighlightReel.Select(
        [
            Make("wp", InsightKind.WinProbability, 5, 60, 1),
            Make("po", InsightKind.PredictionOpened, 4, 120, 2),
            Make("rc", InsightKind.Recap, 5, 2700, 3),
            Make("low", InsightKind.Stat, 2, 300, 4),
            Make("goal", InsightKind.KeyMoment, 5, 600, 5),
        ]);

        Assert.Equal(["goal"], reel.Select(i => i.Id));
    }

    [Fact]
    public void Keeps_the_highest_priority_moments_in_match_order()
    {
        var reel = HighlightReel.Select(
        [
            Make("late-goal", InsightKind.KeyMoment, 5, 5000, 10),
            Make("stat", InsightKind.Stat, 3, 100, 11),
            Make("swing", InsightKind.KeyMoment, 4, 2000, 12),
            Make("early-goal", InsightKind.KeyMoment, 5, 900, 13),
        ], count: 3);

        Assert.Equal(["early-goal", "swing", "late-goal"], reel.Select(i => i.Id));
    }

    [Fact]
    public void Shows_each_source_event_only_once()
    {
        var reel = HighlightReel.Select(
        [
            Make("goal", InsightKind.KeyMoment, 5, 600, 7),
            Make("shot", InsightKind.Stat, 3, 600, 7),
        ]);

        Assert.Equal(["goal"], reel.Select(i => i.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void Rejects_out_of_range_counts(int max) =>
        Assert.False(Validation.TryHighlightCount(max, out _, out _));

    [Fact]
    public void Defaults_count_when_not_given()
    {
        Assert.True(Validation.TryHighlightCount(null, out var count, out _));
        Assert.Equal(HighlightReel.DefaultCount, count);
    }
}
