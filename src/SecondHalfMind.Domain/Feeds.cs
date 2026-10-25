namespace SecondHalfMind.Domain;

/// <summary>
/// One item from a live match feed: the event plus, when the provider has it, the ball tracking
/// frames around it. Tracking is written before the event so auto-eventing can read it on arrival.
/// </summary>
public sealed record FeedItem(MatchEvent Event, BallTrack? Track);

/// <summary>
/// A source of match data in real time. The synthetic simulator is the only implementation in this
/// repo; a broadcaster adapter (event feed plus optical tracking) would implement the same contract
/// and translate provider IDs and formats into <see cref="MatchEvent"/> and <see cref="BallTrack"/>.
/// The platform owns writing to the append-only stores, so adapters never get write access and the
/// agents never know which feed is behind the data.
/// </summary>
public interface IMatchFeed
{
    /// <summary>Short identifier shown in logs and the audit trail, e.g. "synthetic".</summary>
    string Name { get; }

    /// <summary>
    /// Streams items for a registered match as they become due, in sequence order. Live adapters
    /// ignore <paramref name="speed"/>; replay sources use it to accelerate the clock.
    /// </summary>
    IAsyncEnumerable<FeedItem> StreamAsync(MatchInfo match, double speed, CancellationToken cancellationToken);
}
