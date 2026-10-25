using SecondHalfMind.Agents.Analytics;
using SecondHalfMind.Agents.Vision;
using SecondHalfMind.Data;
using SecondHalfMind.Domain;
using SecondHalfMind.Simulation;

namespace SecondHalfMind.Tests;

public class AutoEventingTests
{
    private static MatchEvent Event(MatchEventType type, double x, double y, double endX, double endY, double speedKmh, long sequence = 42) => new()
    {
        Id = $"m1-{sequence}",
        MatchId = "m1",
        Sequence = sequence,
        ClockSeconds = 600,
        Type = type,
        Team = Side.Home,
        X = x,
        Y = y,
        EndX = endX,
        EndY = endY,
        SpeedKmh = speedKmh,
    };

    [Theory]
    [InlineData(MatchEventType.Pass, 30, 20, 45, 30, 50)]
    [InlineData(MatchEventType.Pass, 40, 34, 46, 36, 36)]
    [InlineData(MatchEventType.Pass, 20, 10, 70, 55, 95)]
    [InlineData(MatchEventType.Shot, 85, 30, 105, 34, 115)]
    public void Recovers_distance_and_speed_from_noisy_frames(MatchEventType type, double x, double y, double endX, double endY, double speedKmh)
    {
        var e = Event(type, x, y, endX, endY, speedKmh);
        var track = TrackingSynthesizer.ForEvent(e)!;

        var kick = BallTrackAnalyzer.Analyze(track);

        Assert.NotNull(kick);
        Assert.InRange(kick.DistanceM, PassDifficulty.Length(e) - 0.5, PassDifficulty.Length(e) + 0.5);
        Assert.InRange(kick.SpeedKmh, speedKmh * 0.9, speedKmh * 1.1);
    }

    [Fact]
    public void Tracks_are_deterministic_and_drop_some_frames()
    {
        var e = Event(MatchEventType.Pass, 20, 10, 70, 55, 60);

        var a = TrackingSynthesizer.ForEvent(e)!;
        var b = TrackingSynthesizer.ForEvent(e)!;

        Assert.Equal(a.Samples, b.Samples);
        var expectedFrames = a.Samples[^1].T * TrackingSynthesizer.FrameRateHz + 1;
        Assert.True(a.Samples.Count < expectedFrames);
    }

    [Fact]
    public void Events_without_ball_travel_have_no_track()
    {
        var tackle = Event(MatchEventType.Tackle, 30, 20, 30, 20, 1) with { EndX = null, EndY = null, SpeedKmh = null };

        Assert.Null(TrackingSynthesizer.ForEvent(tackle));
    }

    [Fact]
    public void Tracking_store_is_append_only()
    {
        var store = new InMemoryTrackingStore();
        var track = TrackingSynthesizer.ForEvent(Event(MatchEventType.Pass, 30, 20, 45, 30, 50))!;

        store.Append(track);

        Assert.Throws<InvalidOperationException>(() => store.Append(track));
        Assert.Same(track, store.GetBallTrack("m1", 42));
    }
}
