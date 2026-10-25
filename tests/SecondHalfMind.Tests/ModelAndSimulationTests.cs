using SecondHalfMind.Agents.Analytics;
using SecondHalfMind.Agents.Models;
using SecondHalfMind.Api;
using SecondHalfMind.Data;
using SecondHalfMind.Domain;
using SecondHalfMind.Simulation;

namespace SecondHalfMind.Tests;

public class SimulationTests
{
    private static readonly LeagueStore League = new(CalibrationProfile.Defaults);

    [Fact]
    public void League_is_fictional_and_complete()
    {
        Assert.Equal(20, League.Clubs.Count);
        Assert.All(League.Clubs, c => Assert.Equal(23, c.Squad.Count));
        var names = League.Clubs.SelectMany(c => c.Squad).Select(p => p.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(3 * 380, League.History.Results.Count);
    }

    [Fact]
    public void Simulation_is_deterministic_for_a_seed()
    {
        var match = new MatchInfo("t1", "northvale", "redcastle", 42, DateTimeOffset.UnixEpoch);
        var a = MatchSimulator.Simulate(match, League.GetClub("northvale")!, League.GetClub("redcastle")!, League.Calibration);
        var b = MatchSimulator.Simulate(match, League.GetClub("northvale")!, League.GetClub("redcastle")!, League.Calibration);

        Assert.True(a.SequenceEqual(b));
    }

    [Fact]
    public void Events_are_ordered_and_bracketed_by_kick_off_and_full_time()
    {
        var match = new MatchInfo("t2", "ashbury", "ravenport", 9, DateTimeOffset.UnixEpoch);
        var events = MatchSimulator.Simulate(match, League.GetClub("ashbury")!, League.GetClub("ravenport")!, League.Calibration);

        Assert.Equal(MatchEventType.KickOff, events[0].Type);
        Assert.Equal(MatchEventType.FullTime, events[^1].Type);
        Assert.Single(events, e => e.Type == MatchEventType.HalfTime);
        for (var i = 1; i < events.Count; i++)
        {
            Assert.True(events[i].Sequence > events[i - 1].Sequence);
            Assert.True(events[i].ClockSeconds >= events[i - 1].ClockSeconds);
        }
    }

    [Fact]
    public void Averages_track_the_calibration_profile()
    {
        var clubs = League.Clubs;
        double goals = 0, shots = 0;
        const int Matches = 120;
        for (var i = 0; i < Matches; i++)
        {
            var home = clubs[i % 20];
            var away = clubs[(i * 7 + 3) % 20];
            if (home == away)
            {
                away = clubs[(i + 1) % 20];
            }

            var events = MatchSimulator.Simulate(new MatchInfo($"avg{i}", home.Id, away.Id, i, DateTimeOffset.UnixEpoch), home, away, League.Calibration);
            goals += events.Count(e => e.Type == MatchEventType.Goal);
            shots += events.Count(e => e.Type == MatchEventType.Shot);
        }

        var cal = League.Calibration;
        Assert.InRange(goals / Matches, 0.8 * (cal.MeanHomeGoals + cal.MeanAwayGoals), 1.25 * (cal.MeanHomeGoals + cal.MeanAwayGoals));
        Assert.InRange(shots / Matches / 2, 0.75 * cal.ShotsPerTeamMean, 1.3 * cal.ShotsPerTeamMean);
    }
}

public class ModelTests
{
    private static readonly LeagueStore League = new(CalibrationProfile.Defaults);
    private static readonly PreMatchModel Model = new(League);

    [Fact]
    public void Outcome_probabilities_sum_to_one()
    {
        var p = Poisson.Outcomes(1.6, 1.2);

        Assert.InRange(p.Home + p.Draw + p.Away, 0.999, 1.001);
        Assert.True(p.Home > p.Away);
    }

    [Fact]
    public void Model_prefers_the_stronger_side()
    {
        var ranked = League.Clubs.OrderByDescending(c => Model.Rating(c.Id).Attack / Model.Rating(c.Id).Defense).ToList();
        var call = Model.Predict(ranked[0], ranked[^1]);

        Assert.Equal(MatchOutcome.Home, call.Prediction.Outcome);
        Assert.NotNull(call.Probabilities);
        Assert.NotEmpty(call.Factors);
    }

    [Fact]
    public void Live_win_probability_follows_the_score_and_the_clock()
    {
        var empty = new WindowMetrics(0, 600, new SideWindow(0, 0, 0, 0, 0, 0, 0, 0), new SideWindow(0, 0, 0, 0, 0, 0, 0, 0), 0);
        MatchSnapshot Snapshot(int clock, int homeGoals) => new("m", 1, clock, MatchPhase.SecondHalf,
            TeamStats.Empty with { Goals = homeGoals }, TeamStats.Empty, [], new Dictionary<string, PlayerLive>());

        var early = InPlayModel.WinProbability(Snapshot(600, 1), 1.5, 1.2, empty);
        var late = InPlayModel.WinProbability(Snapshot(5200, 1), 1.5, 1.2, empty);

        Assert.True(late.Home > early.Home, "A lead is worth more the later it is.");
        Assert.True(late.Home > 0.8);
    }

    [Fact]
    public void Pass_difficulty_rewards_long_progressive_passes_under_pressure()
    {
        MatchEvent Pass(double x, double endX, bool pressure) => new()
        {
            Id = "p", MatchId = "m", Sequence = 1, ClockSeconds = 60, Type = MatchEventType.Pass, Team = Side.Home,
            X = x, Y = 34, EndX = endX, EndY = 34, Success = true, UnderPressure = pressure,
        };

        var easy = PassDifficulty.Rate(Pass(40, 45, false));
        var hard = PassDifficulty.Rate(Pass(55, 95, true));

        Assert.True(hard > easy);
        Assert.InRange(hard, 8, 10);
    }
}

public class ValidationTests
{
    private static readonly LeagueStore League = new(CalibrationProfile.Defaults);
    private static readonly MatchInfo Match = new("m", "northvale", "redcastle", 1, DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("Prash", true)]
    [InlineData("Fan_99 - CBE", true)]
    [InlineData("x", false)]
    [InlineData("<script>", false)]
    [InlineData("Official Admin", false)]
    [InlineData("ThisNicknameIsWayTooLongToAccept", false)]
    public void Nicknames_are_allow_listed(string nickname, bool expected)
    {
        Assert.Equal(expected, Validation.IsValidNickname(nickname));
    }

    [Fact]
    public void Profiles_only_accept_known_values()
    {
        var player = League.GetClub("northvale")!.Squad[9].Id;
        var outsider = League.GetClub("ashbury")!.Squad[9].Id;

        Assert.True(Validation.TryProfile(new ProfileDto("analyst", "hi", "northvale", player, "passDifficulty"), Match, League, out var profile, out _));
        Assert.Equal(ViewerMode.Analyst, profile.Mode);
        Assert.False(Validation.TryProfile(new ProfileDto("casual", "xx", null, null, null), Match, League, out _, out _));
        Assert.False(Validation.TryProfile(new ProfileDto("casual", "en", null, outsider, null), Match, League, out _, out _));
        Assert.False(Validation.TryProfile(new ProfileDto("casual", "en", null, null, "odds"), Match, League, out _, out _));
    }

    [Fact]
    public void Picks_must_be_consistent()
    {
        var fan = Guid.NewGuid().ToString();

        Assert.True(Validation.TryPick(new PickRequest(fan, "Fan", "Home", 2, 1), "m", out _, out _));
        Assert.False(Validation.TryPick(new PickRequest(fan, "Fan", "Away", 2, 1), "m", out _, out _));
        Assert.False(Validation.TryPick(new PickRequest(fan, "Fan", "Home", 12, 1), "m", out _, out _));
        Assert.False(Validation.TryPick(new PickRequest("not-a-guid", "Fan", "Home", 2, 1), "m", out _, out _));
    }
}
