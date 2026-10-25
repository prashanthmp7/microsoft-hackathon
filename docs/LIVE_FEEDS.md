# Live feed integration

Second Half Mind runs on a synthetic feed. This note explains how a broadcaster feed (live event data plus optical tracking) plugs in, and what is not built yet. No real provider data, names or IDs belong in this repo.

## The seam: `IMatchFeed`

`src/SecondHalfMind.Domain/Feeds.cs`

```csharp
public sealed record FeedItem(MatchEvent Event, BallTrack? Track);

public interface IMatchFeed
{
	string Name { get; }
	IAsyncEnumerable<FeedItem> StreamAsync(MatchInfo match, double speed, CancellationToken ct);
}
```

- `SyntheticMatchFeed` (in `src/SecondHalfMind.Api/Services`) is the only implementation. It wraps `MatchSimulator` and `TrackingSynthesizer`, produces 25 Hz ball tracks (the same rate as the main optical systems), and paces items in accelerated real time.
- `MatchRunner` consumes whichever `IMatchFeed` is registered in `Program.cs`. It writes the track first, then the event, to the append-only stores, and mirrors the event to Event Hubs.
- Adapters get no write capability. The platform does all writing, and the agents only see `MatchEvent` and `BallTrack` through read-only interfaces, so they cannot tell which feed is behind the data.

## Adding a broadcaster adapter

1. Implement `IMatchFeed` in a new project (for example `SecondHalfMind.Feeds.<Provider>`) that references only `SecondHalfMind.Domain`.
2. Connect to the provider's push or poll endpoint. Store credentials in Key Vault and use managed identity where the provider supports it.
3. Translate each provider message:
   - Map provider club, player and match IDs to internal IDs through a mapping table. Never put real names in the repo.
   - Map event types and qualifiers onto `MatchEventType` and the `MatchEvent` fields.
   - Convert coordinates to metres on the 105 x 68 pitch, with home attacking left to right.
   - Slice tracking frames around each ball event into a `BallTrack`. Keep each frame's own timestamp, because frames can be missing.
4. Yield items in sequence order. Ignore `speed`, which applies only to replays.
5. Register the adapter in place of `SyntheticMatchFeed` in `Program.cs`. No agent changes are needed.

## Not built yet (roadmap)

| Gap | Planned approach |
|---|---|
| Corrections (re-typed events, a goal credited to another player, deleted events) | Keep the store append-only. Add a `Correction` event that references the original sequence and carries the new values. Read models apply corrections on read. Agents emit follow-up insights such as "goal now credited to …" and never edit published ones. |
| Late or out-of-order delivery | Buffer briefly in the adapter and reorder by provider sequence. Anything later than the buffer becomes a correction. |
| Player tracking (all 22 players) | Add a `PlayerFrame` stream next to `BallTrack`, behind a new read-only interface, to enable pressing, shape and off-ball metrics. |
| Feed outages | Adapter heartbeats, a "data delayed" overlay state, and resuming from the last acknowledged sequence. |
| Scale | Swap the in-memory stores for durable storage (Fabric Eventhouse), with one runner per match partition. |
| Licensing | Live event and tracking feeds are licensed products. They need a commercial agreement and are out of scope for the hackathon. |
