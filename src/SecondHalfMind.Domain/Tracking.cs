namespace SecondHalfMind.Domain;

/// <summary>
/// One ball position from a (synthetic) optical tracking system. T is seconds from the start of
/// the clip; X and Y are metres on the same 105 x 68 pitch as <see cref="MatchEvent"/>.
/// </summary>
public sealed record BallSample(double T, double X, double Y);

/// <summary>
/// Ball tracking frames around one event. Kept separate from the event so auto-eventing has to
/// derive distance and speed from positions alone, the way it would from a video model.
/// Frames can be missing (occlusion), so consumers must use each sample's own timestamp.
/// </summary>
public sealed record BallTrack(string MatchId, long Sequence, int FrameRateHz, IReadOnlyList<BallSample> Samples);

/// <summary>Read-only access to tracking frames. Agents may depend on this.</summary>
public interface ITrackingReadModel
{
    BallTrack? GetBallTrack(string matchId, long sequence);
}

/// <summary>Platform-only ingest of tracking frames. Append-only, like the event store.</summary>
[DataWriteCapability]
public interface ITrackingWriter
{
    void Append(BallTrack track);
}
