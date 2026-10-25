using SecondHalfMind.Api.Services;
using SecondHalfMind.Data;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Tests;

/// <summary>The synthetic feed must look like a live feed: ordered, tracked, and swappable.</summary>
public class MatchFeedTests
{
    [Fact]
    public async Task Synthetic_feed_streams_ordered_events_with_tracking_for_passes_and_shots()
    {
        var league = new LeagueStore(CalibrationProfile.Defaults);
        var store = new InMemoryMatchStore(league);
        var match = store.Register("northvale", "redcastle", 7);
        IMatchFeed feed = new SyntheticMatchFeed(league);

        var items = new List<FeedItem>();
        await foreach (var item in feed.StreamAsync(match, speed: 1_000_000, CancellationToken.None))
        {
            items.Add(item);
        }

        Assert.Equal("synthetic", feed.Name);
        Assert.NotEmpty(items);
        Assert.Equal(MatchEventType.FullTime, items[^1].Event.Type);
        Assert.True(items.Select(i => i.Event.Sequence).SequenceEqual(items.Select(i => i.Event.Sequence).Order()));
        Assert.All(items.Where(i => i.Track is not null), i =>
        {
            Assert.Equal(i.Event.Sequence, i.Track!.Sequence);
            Assert.Equal(25, i.Track.FrameRateHz);
        });
        Assert.Contains(items, i => i.Event.Type == MatchEventType.Shot && i.Track is not null);
    }

    [Fact]
    public void Feed_contract_lives_in_domain_so_adapters_need_no_platform_references()
    {
        Assert.Equal(typeof(MatchEvent).Assembly, typeof(IMatchFeed).Assembly);
        Assert.Empty(typeof(IMatchFeed).GetCustomAttributes(typeof(DataWriteCapabilityAttribute), false));
    }
}
