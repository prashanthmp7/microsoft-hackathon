using SecondHalfMind.Domain;

namespace SecondHalfMind.Simulation;

public static class StrengthModel
{
    public static (double Home, double Away) ExpectedGoals(Club home, Club away, CalibrationProfile calibration) =>
        (calibration.MeanHomeGoals * home.Attack * away.Defense,
         calibration.MeanAwayGoals * away.Attack * home.Defense);
}

/// <summary>
/// Generates past seasons of results for the synthetic league (double round robin). These are the
/// "historical" data the prediction model is trained on. Strengths drift slightly between seasons.
/// </summary>
public static class HistoryGenerator
{
    public static LeagueHistory Generate(IReadOnlyList<Club> clubs, CalibrationProfile calibration, int seasons = 3, int seed = 7)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(clubs.Count, 2);
        var random = new Random(seed);
        var seasonNames = new List<string>(seasons);
        var results = new List<HistoricalResult>();

        for (var s = 0; s < seasons; s++)
        {
            // Older seasons drift further from current strength.
            var yearsAgo = seasons - s;
            var drift = clubs.ToDictionary(
                c => c.Id,
                _ => (Attack: Math.Exp(random.NextGaussian(0, 0.06 * yearsAgo)), Defense: Math.Exp(random.NextGaussian(0, 0.06 * yearsAgo))));

            var seasonName = $"Synthetic Season {s + 1}";
            seasonNames.Add(seasonName);
            var round = 0;
            foreach (var fixtures in DoubleRoundRobin(clubs))
            {
                round++;
                foreach (var (home, away) in fixtures)
                {
                    var (lh, la) = StrengthModel.ExpectedGoals(home, away, calibration);
                    lh *= drift[home.Id].Attack * drift[away.Id].Defense;
                    la *= drift[away.Id].Attack * drift[home.Id].Defense;
                    results.Add(new HistoricalResult(seasonName, round, home.Id, away.Id, random.NextPoisson(lh), random.NextPoisson(la)));
                }
            }
        }

        return new LeagueHistory(seasonNames, results);
    }

    /// <summary>Circle-method schedule: each club plays every other club home and away.</summary>
    public static IEnumerable<IReadOnlyList<(Club Home, Club Away)>> DoubleRoundRobin(IReadOnlyList<Club> clubs)
    {
        var teams = clubs.ToList();
        var n = teams.Count;
        var rounds = new List<IReadOnlyList<(Club, Club)>>();
        for (var r = 0; r < n - 1; r++)
        {
            var fixtures = new List<(Club, Club)>(n / 2);
            for (var i = 0; i < n / 2; i++)
            {
                var a = teams[i];
                var b = teams[n - 1 - i];
                fixtures.Add(r % 2 == 0 ? (a, b) : (b, a));
            }

            rounds.Add(fixtures);

            // Rotate all but the first team.
            var last = teams[n - 1];
            teams.RemoveAt(n - 1);
            teams.Insert(1, last);
        }

        foreach (var round in rounds)
        {
            yield return round;
        }

        foreach (var round in rounds)
        {
            yield return round.Select(f => (f.Item2, f.Item1)).ToList();
        }
    }
}
