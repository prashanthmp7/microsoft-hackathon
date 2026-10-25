using SecondHalfMind.Agents.Analytics;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents.Models;

/// <summary>
/// Live win probability: the pre-match expected goals are scaled by time remaining, recent momentum
/// (share of passes and pressing in the last ten minutes) and red cards, then combined with the
/// current score via a Poisson grid.
/// </summary>
public static class InPlayModel
{
    public const double MomentumWeight = 1.0;

    public static OutcomeProbabilities WinProbability(MatchSnapshot snapshot, double lambdaHome, double lambdaAway, WindowMetrics recent)
    {
        if (snapshot.Phase == MatchPhase.FullTime)
        {
            var diff = snapshot.Home.Goals - snapshot.Away.Goals;
            return diff > 0 ? new(1, 0, 0) : diff == 0 ? new(0, 1, 0) : new(0, 0, 1);
        }

        var remaining = Math.Clamp((5400.0 - snapshot.ClockSeconds) / 5400.0, 0, 1);
        // Momentum is shrunk towards zero when the window holds few passes, so a handful of early
        // events can't swing the numbers.
        var sample = recent.Home.Passes + recent.Away.Passes;
        var confidence = sample / (sample + 60.0);
        var momentum = confidence * (0.5 * (recent.HomePassShare - 0.5) + 0.5 * (recent.HomePressureShare - 0.5));
        var homeFactor = Math.Exp(MomentumWeight * momentum);
        var awayFactor = Math.Exp(-MomentumWeight * momentum);

        var redHome = snapshot.Home.RedCards;
        var redAway = snapshot.Away.RedCards;
        var lh = lambdaHome * remaining * homeFactor * Math.Pow(0.7, redHome) * Math.Pow(1.25, redAway);
        var la = lambdaAway * remaining * awayFactor * Math.Pow(0.7, redAway) * Math.Pow(1.25, redHome);

        return Poisson.Outcomes(lh, la, snapshot.Home.Goals, snapshot.Away.Goals);
    }

    public static double For(this OutcomeProbabilities p, Side side) => side == Side.Home ? p.Home : p.Away;
}
