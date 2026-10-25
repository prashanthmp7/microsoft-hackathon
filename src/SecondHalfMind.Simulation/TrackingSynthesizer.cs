using SecondHalfMind.Domain;

namespace SecondHalfMind.Simulation;

/// <summary>
/// Synthetic optical tracking. For each pass and shot it renders the ball's path as 25 Hz frames
/// with measurement noise and occasional dropped frames, so the auto-eventing agent has to
/// recover distance and speed from positions alone. Deterministic per event, so replays match.
/// </summary>
public static class TrackingSynthesizer
{
    public const int FrameRateHz = 25;
    public const double PositionNoiseM = 0.15;
    public const double DropRate = 0.05;
    private const double StillSeconds = 0.4;

    public static BallTrack? ForEvent(MatchEvent e)
    {
        if (e.Type is not (MatchEventType.Pass or MatchEventType.Shot)
            || e.EndX is not { } endX || e.EndY is not { } endY || e.SpeedKmh is not { } speedKmh || speedKmh <= 0)
        {
            return null;
        }

        var random = new Random(unchecked((int)(e.Sequence * 7919) ^ e.ClockSeconds));
        var dx = endX - e.X;
        var dy = endY - e.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        var flight = length / (speedKmh / 3.6);
        var total = StillSeconds + flight + StillSeconds;
        var dt = 1.0 / FrameRateHz;
        var frames = (int)Math.Ceiling(total / dt) + 1;

        var samples = new List<BallSample>(frames);
        for (var i = 0; i < frames; i++)
        {
            var t = i * dt;
            var progress = flight <= 0 ? 1 : Math.Clamp((t - StillSeconds) / flight, 0, 1);

            // Keep the first and last frames so the clip always has a start and an end position.
            if (i > 0 && i < frames - 1 && random.Chance(DropRate))
            {
                continue;
            }

            samples.Add(new BallSample(
                Math.Round(t, 3),
                Math.Round(e.X + dx * progress + random.NextGaussian(0, PositionNoiseM), 2),
                Math.Round(e.Y + dy * progress + random.NextGaussian(0, PositionNoiseM), 2)));
        }

        return new BallTrack(e.MatchId, e.Sequence, FrameRateHz, samples);
    }
}
