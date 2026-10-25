using System.Collections.Concurrent;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Data;

/// <summary>
/// Append-only store for synthetic ball-tracking frames, kept apart from events so the
/// auto-eventing agent reads raw positions rather than tagged results. A track for a given
/// event can be written once and never changed.
/// </summary>
public sealed class InMemoryTrackingStore : ITrackingReadModel, ITrackingWriter
{
    private const int MaxTracksPerMatch = 20_000;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, BallTrack>> _byMatch = new(StringComparer.Ordinal);

    public void Append(BallTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var tracks = _byMatch.GetOrAdd(track.MatchId, _ => new ConcurrentDictionary<long, BallTrack>());
        if (tracks.Count >= MaxTracksPerMatch)
        {
            return;
        }

        if (!tracks.TryAdd(track.Sequence, track))
        {
            throw new InvalidOperationException($"Tracking for sequence {track.Sequence} already exists; tracks are append-only.");
        }
    }

    public BallTrack? GetBallTrack(string matchId, long sequence) =>
        _byMatch.TryGetValue(matchId, out var tracks) && tracks.TryGetValue(sequence, out var track) ? track : null;
}
