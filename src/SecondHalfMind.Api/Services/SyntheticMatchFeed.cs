using System.Runtime.CompilerServices;
using SecondHalfMind.Domain;
using SecondHalfMind.Simulation;

namespace SecondHalfMind.Api.Services;

/// <summary>
/// Feed adapter backed by the synthetic simulator and 25 Hz tracking synthesizer. Stands in for a
/// broadcaster event feed plus optical tracking, at the same frame rate, paced in accelerated real time.
/// </summary>
public sealed class SyntheticMatchFeed(ILeagueReadModel league) : IMatchFeed
{
    private const int HalfTimeBreakSeconds = 120;

    public string Name => "synthetic";

    public async IAsyncEnumerable<FeedItem> StreamAsync(MatchInfo match, double speed, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var home = league.GetClub(match.HomeClubId);
        var away = league.GetClub(match.AwayClubId);
        if (home is null || away is null)
        {
            yield break;
        }

        var started = DateTimeOffset.UtcNow;
        var afterHalfTime = false;
        foreach (var e in MatchSimulator.Simulate(match, home, away, league.Calibration))
        {
            // Real time = match time / speed, plus a short half-time break (in match seconds).
            var matchSeconds = e.ClockSeconds + (afterHalfTime ? HalfTimeBreakSeconds : 0);
            afterHalfTime |= e.Type == MatchEventType.HalfTime;
            var wait = started + TimeSpan.FromSeconds(matchSeconds / speed) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken);
            }

            yield return new FeedItem(e, TrackingSynthesizer.ForEvent(e));
        }
    }
}
