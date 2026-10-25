using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents.Models;

public static class Poisson
{
    private const int MaxGoals = 10;

    public static double Pmf(int k, double lambda)
    {
        if (lambda <= 0)
        {
            return k == 0 ? 1 : 0;
        }

        var logP = -lambda + k * Math.Log(lambda);
        for (var i = 2; i <= k; i++)
        {
            logP -= Math.Log(i);
        }

        return Math.Exp(logP);
    }

    /// <summary>Outcome probabilities given goals already scored and expected goals still to come.</summary>
    public static OutcomeProbabilities Outcomes(double lambdaHome, double lambdaAway, int homeNow = 0, int awayNow = 0)
    {
        double home = 0, draw = 0, away = 0;
        for (var h = 0; h <= MaxGoals; h++)
        {
            var ph = Pmf(h, lambdaHome);
            for (var a = 0; a <= MaxGoals; a++)
            {
                var p = ph * Pmf(a, lambdaAway);
                var diff = (homeNow + h) - (awayNow + a);
                if (diff > 0)
                {
                    home += p;
                }
                else if (diff == 0)
                {
                    draw += p;
                }
                else
                {
                    away += p;
                }
            }
        }

        var total = home + draw + away;
        return new OutcomeProbabilities(Math.Round(home / total, 4), Math.Round(draw / total, 4), Math.Round(away / total, 4));
    }

    /// <summary>The most likely exact score consistent with the most likely outcome.</summary>
    public static ScorePrediction MostLikelyScore(double lambdaHome, double lambdaAway)
    {
        var outcome = Outcomes(lambdaHome, lambdaAway).MostLikely;
        var best = (H: 0, A: 0, P: -1.0);
        for (var h = 0; h <= 6; h++)
        {
            for (var a = 0; a <= 6; a++)
            {
                var matches = outcome switch
                {
                    MatchOutcome.Home => h > a,
                    MatchOutcome.Away => a > h,
                    _ => h == a,
                };
                var p = Pmf(h, lambdaHome) * Pmf(a, lambdaAway);
                if (matches && p > best.P)
                {
                    best = (h, a, p);
                }
            }
        }

        return new ScorePrediction(outcome, best.H, best.A);
    }
}
