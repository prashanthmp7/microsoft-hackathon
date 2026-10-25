using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents.Analytics;

public sealed record SideWindow(int Passes, int PassesCompleted, int Pressure, int Shots, int ShotsOnTarget, int Corners, int BallWins, int Goals)
{
    public double Completion => Passes == 0 ? 0 : (double)PassesCompleted / Passes;
}

/// <summary>Metrics over a slice of match time. The building block for control, chaos, rhythm and pressure.</summary>
public sealed record WindowMetrics(int FromClock, int ToClock, SideWindow Home, SideWindow Away, int PossessionChanges)
{
    public double Minutes => Math.Max(1, (ToClock - FromClock) / 60.0);

    public double HomePassShare => Share(Home.Passes, Away.Passes);

    public double HomePressureShare => Share(Home.Pressure, Away.Pressure);

    public double TurnoversPerMinute => PossessionChanges / Minutes;

    /// <summary>Passes per minute, both sides combined.</summary>
    public double Tempo => (Home.Passes + Away.Passes) / Minutes;

    public SideWindow For(Side side) => side == Side.Home ? Home : Away;

    public double PassShare(Side side) => side == Side.Home ? HomePassShare : 1 - HomePassShare;

    public double PressureShare(Side side) => side == Side.Home ? HomePressureShare : 1 - HomePressureShare;

    /// <summary>0..100: how much one side dictates play (share of the ball and how cleanly it is kept).</summary>
    public int ControlIndex(Side side) =>
        (int)Math.Round(100 * Math.Clamp(0.65 * PassShare(side) + 0.35 * For(side).Completion, 0, 1));

    /// <summary>0..100: how broken the game is (frequent turnovers, shots at both ends).</summary>
    public int ChaosIndex
    {
        get
        {
            var turnoverPart = Math.Clamp((TurnoversPerMinute - 2.0) / 3.0, 0, 1);
            var bothEnds = Math.Min(Home.Shots, 2) + Math.Min(Away.Shots, 2);
            var shotPart = Math.Clamp(bothEnds / 4.0, 0, 1);
            return (int)Math.Round(100 * (0.7 * turnoverPart + 0.3 * shotPart));
        }
    }

    public static WindowMetrics Compute(IEnumerable<MatchEvent> events, int fromClock, int toClock)
    {
        var home = new Counter();
        var away = new Counter();
        var possessionChanges = 0;

        foreach (var e in events)
        {
            if (e.ClockSeconds < fromClock || e.ClockSeconds > toClock)
            {
                continue;
            }

            var c = e.Team == Side.Home ? home : away;
            switch (e.Type)
            {
                case MatchEventType.Pass:
                    c.Passes++;
                    if (e.Success == true)
                    {
                        c.Completed++;
                    }

                    break;
                case MatchEventType.Pressure:
                    c.Pressure++;
                    break;
                case MatchEventType.Shot:
                    c.Shots++;
                    if (e.OnTarget == true)
                    {
                        c.OnTarget++;
                    }

                    break;
                case MatchEventType.Corner:
                    c.Corners++;
                    break;
                case MatchEventType.Tackle or MatchEventType.Interception:
                    c.BallWins++;
                    break;
                case MatchEventType.Goal:
                    c.Goals++;
                    break;
                case MatchEventType.PossessionChange:
                    possessionChanges++;
                    break;
            }
        }

        return new WindowMetrics(fromClock, toClock, home.ToWindow(), away.ToWindow(), possessionChanges);
    }

    private static double Share(int a, int b) => a + b == 0 ? 0.5 : (double)a / (a + b);

    private sealed class Counter
    {
        public int Passes, Completed, Pressure, Shots, OnTarget, Corners, BallWins, Goals;

        public SideWindow ToWindow() => new(Passes, Completed, Pressure, Shots, OnTarget, Corners, BallWins, Goals);
    }
}

public static class PassDifficulty
{
    /// <summary>
    /// 0..10 rating of how hard a pass was: length, how far it moved the ball forward, pressure on the
    /// passer and whether it reached the final third.
    /// </summary>
    public static double Rate(MatchEvent pass)
    {
        if (pass.Type != MatchEventType.Pass || pass.EndX is null || pass.EndY is null)
        {
            return 0;
        }

        var length = Length(pass);
        var progression = Progression(pass);
        var endA = pass.Team == Side.Home ? pass.EndX.Value : 105 - pass.EndX.Value;
        var score = 1.0
            + 4.0 * Math.Clamp((length - 5) / 35, 0, 1)
            + 2.0 * Math.Clamp(progression / 25, 0, 1)
            + (pass.UnderPressure ? 2.0 : 0)
            + (endA > 88 ? 1.0 : 0);
        return Math.Round(Math.Clamp(score, 0, 10), 1);
    }

    public static double Length(MatchEvent pass) =>
        pass.EndX is null || pass.EndY is null ? 0 : Math.Round(Math.Sqrt(Math.Pow(pass.EndX.Value - pass.X, 2) + Math.Pow(pass.EndY.Value - pass.Y, 2)), 1);

    /// <summary>Metres gained towards the opponent's goal.</summary>
    public static double Progression(MatchEvent pass) =>
        pass.EndX is null ? 0 : Math.Round(pass.Team == Side.Home ? pass.EndX.Value - pass.X : pass.X - pass.EndX.Value, 1);

    public static double DistanceToGoal(MatchEvent shot)
    {
        var goalX = shot.Team == Side.Home ? 105 : 0;
        return Math.Round(Math.Sqrt(Math.Pow(goalX - shot.X, 2) + Math.Pow(34 - shot.Y, 2)), 1);
    }
}
