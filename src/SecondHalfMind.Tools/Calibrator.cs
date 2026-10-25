using System.Globalization;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Tools;

/// <summary>
/// Reduces real historical results to anonymous league-level priors. Input rows are held in memory
/// only; the output contains no club names, player names or individual matches, so nothing real is
/// redistributed by the app or the repository.
/// </summary>
public static class Calibrator
{
    public sealed record ResultRow(string Season, string Home, string Away, int HomeGoals, int AwayGoals,
        int? HomeShots, int? AwayShots, int? HomeOnTarget, int? AwayOnTarget, int? HomeCorners, int? AwayCorners,
        int? HomeYellows, int? AwayYellows, int? HomeReds, int? AwayReds);

    public static IReadOnlyList<ResultRow> ParseCsv(string season, string csv)
    {
        var lines = csv.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            return [];
        }

        var header = SplitLine(lines[0].TrimStart('﻿'));
        var index = header.Select((name, i) => (name, i)).ToDictionary(x => x.name.Trim(), x => x.i, StringComparer.OrdinalIgnoreCase);
        foreach (var required in new[] { "HomeTeam", "AwayTeam", "FTHG", "FTAG" })
        {
            if (!index.ContainsKey(required))
            {
                throw new InvalidDataException($"Season {season}: column '{required}' missing.");
            }
        }

        var rows = new List<ResultRow>();
        foreach (var line in lines.Skip(1))
        {
            var cells = SplitLine(line);
            string? Cell(string name) => index.TryGetValue(name, out var i) && i < cells.Count ? cells[i].Trim() : null;
            int? Int(string name) => int.TryParse(Cell(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

            var home = Cell("HomeTeam");
            var away = Cell("AwayTeam");
            if (string.IsNullOrEmpty(home) || string.IsNullOrEmpty(away) || Int("FTHG") is not { } hg || Int("FTAG") is not { } ag)
            {
                continue;
            }

            rows.Add(new ResultRow(season, home, away, hg, ag, Int("HS"), Int("AS"), Int("HST"), Int("AST"), Int("HC"), Int("AC"),
                Int("HY"), Int("AY"), Int("HR"), Int("AR")));
        }

        return rows;
    }

    public static CalibrationProfile Compute(IReadOnlyList<ResultRow> rows, string source)
    {
        if (rows.Count == 0)
        {
            throw new InvalidDataException("No results to calibrate from.");
        }

        double Mean(Func<ResultRow, int?> home, Func<ResultRow, int?> away)
        {
            var values = rows.SelectMany(r => new[] { home(r), away(r) }).Where(v => v.HasValue).Select(v => (double)v!.Value).ToList();
            return values.Count == 0 ? double.NaN : values.Average();
        }

        var defaults = CalibrationProfile.Defaults;
        double Or(double value, double fallback) => double.IsNaN(value) ? fallback : Math.Round(value, 3);

        var shots = Mean(r => r.HomeShots, r => r.AwayShots);
        var onTarget = Mean(r => r.HomeOnTarget, r => r.AwayOnTarget);
        var goalsPerTeam = rows.Average(r => (r.HomeGoals + r.AwayGoals) / 2.0);

        var (attackStd, defenseStd) = StrengthSpread(rows);

        return new CalibrationProfile
        {
            Source = source,
            IsDefault = false,
            Seasons = rows.Select(r => r.Season).Distinct().ToList(),
            MatchCount = rows.Count,
            ComputedAt = DateTimeOffset.UtcNow,
            HomeWinRate = Math.Round(rows.Count(r => r.HomeGoals > r.AwayGoals) / (double)rows.Count, 3),
            DrawRate = Math.Round(rows.Count(r => r.HomeGoals == r.AwayGoals) / (double)rows.Count, 3),
            AwayWinRate = Math.Round(rows.Count(r => r.HomeGoals < r.AwayGoals) / (double)rows.Count, 3),
            MeanHomeGoals = Math.Round(rows.Average(r => r.HomeGoals), 3),
            MeanAwayGoals = Math.Round(rows.Average(r => r.AwayGoals), 3),
            AttackLogStd = Math.Round(attackStd, 3),
            DefenseLogStd = Math.Round(defenseStd, 3),
            ShotsPerTeamMean = Or(shots, defaults.ShotsPerTeamMean),
            ShotsOnTargetRate = double.IsNaN(shots) || double.IsNaN(onTarget) ? defaults.ShotsOnTargetRate : Math.Round(onTarget / shots, 3),
            GoalsPerShot = double.IsNaN(shots) ? defaults.GoalsPerShot : Math.Round(goalsPerTeam / shots, 3),
            CornersPerTeamMean = Or(Mean(r => r.HomeCorners, r => r.AwayCorners), defaults.CornersPerTeamMean),
            YellowCardsPerTeamMean = Or(Mean(r => r.HomeYellows, r => r.AwayYellows), defaults.YellowCardsPerTeamMean),
            RedCardsPerTeamMean = Or(Mean(r => r.HomeReds, r => r.AwayReds), defaults.RedCardsPerTeamMean),
        };
    }

    /// <summary>Fits Poisson attack/defence strengths per season, then returns the spread of their logs.</summary>
    private static (double AttackStd, double DefenseStd) StrengthSpread(IReadOnlyList<ResultRow> rows)
    {
        var attackLogs = new List<double>();
        var defenseLogs = new List<double>();
        foreach (var season in rows.GroupBy(r => r.Season))
        {
            var games = season.ToList();
            var teams = games.SelectMany(g => new[] { g.Home, g.Away }).Distinct().ToList();
            var meanHome = games.Average(g => g.HomeGoals);
            var meanAway = games.Average(g => g.AwayGoals);
            var attack = teams.ToDictionary(t => t, _ => 1.0);
            var defense = teams.ToDictionary(t => t, _ => 1.0);

            for (var iteration = 0; iteration < 25; iteration++)
            {
                foreach (var team in teams)
                {
                    double scored = 0, expScored = 0, conceded = 0, expConceded = 0;
                    foreach (var g in games)
                    {
                        if (g.Home == team)
                        {
                            scored += g.HomeGoals;
                            expScored += meanHome * defense[g.Away];
                            conceded += g.AwayGoals;
                            expConceded += meanAway * attack[g.Away];
                        }
                        else if (g.Away == team)
                        {
                            scored += g.AwayGoals;
                            expScored += meanAway * defense[g.Home];
                            conceded += g.HomeGoals;
                            expConceded += meanHome * attack[g.Home];
                        }
                    }

                    attack[team] = expScored > 0 ? Math.Max(0.05, scored / expScored) : 1;
                    defense[team] = expConceded > 0 ? Math.Max(0.05, conceded / expConceded) : 1;
                }

                var am = attack.Values.Average();
                var dm = defense.Values.Average();
                foreach (var team in teams)
                {
                    attack[team] /= am;
                    defense[team] /= dm;
                }
            }

            attackLogs.AddRange(attack.Values.Select(v => Math.Log(v)));
            defenseLogs.AddRange(defense.Values.Select(v => Math.Log(v)));
        }

        static double Std(IReadOnlyList<double> values)
        {
            var mean = values.Average();
            return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, values.Count - 1));
        }

        return (Std(attackLogs), Std(defenseLogs));
    }

    private static List<string> SplitLine(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (ch == ',' && !quoted)
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        cells.Add(current.ToString());
        return cells;
    }
}
