using System.Globalization;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents.Models;

public sealed record TeamRating(string ClubId, double Attack, double Defense, double GoalsForPerGame, double GoalsAgainstPerGame);

public sealed record FormSummary(string ClubId, string Sequence, int Points, int Games)
{
    public double PointsPerGame => Games == 0 ? 0 : (double)Points / Games;
}

public sealed record HeadToHead(int Played, int HomeWins, int Draws, int AwayWins);

/// <summary>
/// Poisson team-strength model fitted on the league's historical results. Recent seasons weigh more.
/// The model only sees results, never the simulator's hidden strengths, so it can be wrong.
/// </summary>
public sealed class PreMatchModel
{
    private const int FitIterations = 25;
    private readonly ILeagueReadModel _league;
    private readonly Dictionary<string, TeamRating> _ratings;
    private readonly double _meanHomeGoals;
    private readonly double _meanAwayGoals;

    public PreMatchModel(ILeagueReadModel league)
    {
        _league = league;
        (_ratings, _meanHomeGoals, _meanAwayGoals) = Fit(league);
    }

    public TeamRating Rating(string clubId) => _ratings[clubId];

    public (double Home, double Away) ExpectedGoals(string homeClubId, string awayClubId)
    {
        var home = _ratings[homeClubId];
        var away = _ratings[awayClubId];
        return (Math.Round(_meanHomeGoals * home.Attack * away.Defense, 3), Math.Round(_meanAwayGoals * away.Attack * home.Defense, 3));
    }

    public FormSummary Form(string clubId, int games = 6)
    {
        var latest = _league.History.Seasons[^1];
        var recent = _league.History.Results
            .Where(r => r.Season == latest && (r.HomeClubId == clubId || r.AwayClubId == clubId))
            .OrderByDescending(r => r.Round)
            .Take(games)
            .Reverse()
            .ToList();

        var sequence = new System.Text.StringBuilder();
        var points = 0;
        foreach (var r in recent)
        {
            var (gf, ga) = r.HomeClubId == clubId ? (r.HomeGoals, r.AwayGoals) : (r.AwayGoals, r.HomeGoals);
            var letter = gf > ga ? 'W' : gf == ga ? 'D' : 'L';
            points += letter == 'W' ? 3 : letter == 'D' ? 1 : 0;
            sequence.Append(letter);
        }

        return new FormSummary(clubId, sequence.ToString(), points, recent.Count);
    }

    public HeadToHead HeadToHead(string homeClubId, string awayClubId)
    {
        var meetings = _league.History.Results
            .Where(r => (r.HomeClubId == homeClubId && r.AwayClubId == awayClubId) || (r.HomeClubId == awayClubId && r.AwayClubId == homeClubId))
            .ToList();
        int homeWins = 0, draws = 0, awayWins = 0;
        foreach (var r in meetings)
        {
            var (g1, g2) = r.HomeClubId == homeClubId ? (r.HomeGoals, r.AwayGoals) : (r.AwayGoals, r.HomeGoals);
            if (g1 > g2)
            {
                homeWins++;
            }
            else if (g1 == g2)
            {
                draws++;
            }
            else
            {
                awayWins++;
            }
        }

        return new HeadToHead(meetings.Count, homeWins, draws, awayWins);
    }

    public PreMatchCall Predict(Club home, Club away)
    {
        var (lh, la) = ExpectedGoals(home.Id, away.Id);
        var probabilities = Poisson.Outcomes(lh, la);
        var score = Poisson.MostLikelyScore(lh, la);
        var hr = _ratings[home.Id];
        var ar = _ratings[away.Id];
        var homeAdvantage = _meanAwayGoals > 0 ? _meanHomeGoals / _meanAwayGoals : 1;

        var factors = new List<PredictionFactor>
        {
            new("Attack strength", $"{home.ShortName} {hr.Attack:0.00} vs {away.ShortName} {ar.Attack:0.00} (1.00 = league average)", Math.Round(hr.Attack - ar.Attack, 2)),
            new("Defensive record", $"Goals conceded per game: {home.ShortName} {hr.GoalsAgainstPerGame:0.00}, {away.ShortName} {ar.GoalsAgainstPerGame:0.00}", Math.Round(ar.GoalsAgainstPerGame - hr.GoalsAgainstPerGame, 2)),
            new("Home advantage", $"Home sides score {homeAdvantage:0.00}x as many goals in this league", Math.Round(homeAdvantage, 2)),
            new("Expected goals", $"{home.ShortName} {lh:0.00}, {away.ShortName} {la:0.00}", Math.Round(lh - la, 2)),
        };

        var favourite = probabilities.MostLikely switch
        {
            MatchOutcome.Home => home.Name,
            MatchOutcome.Away => away.Name,
            _ => "neither side",
        };
        var rationale = string.Create(CultureInfo.InvariantCulture,
            $"Fitted on three seasons of league results. Expected goals {lh:0.00} to {la:0.00} make {favourite} the most likely outcome ({Math.Round(Math.Max(probabilities.Home, Math.Max(probabilities.Draw, probabilities.Away)) * 100):0}%).");

        return new PreMatchCall("Model", score, probabilities, factors, rationale);
    }

    private static (Dictionary<string, TeamRating> Ratings, double MeanHome, double MeanAway) Fit(ILeagueReadModel league)
    {
        var seasons = league.History.Seasons;
        double Weight(HistoricalResult r)
        {
            var seasonsAgo = seasons.Count - 1 - IndexOf(seasons, r.Season);
            return Math.Pow(0.6, seasonsAgo);
        }

        var results = league.History.Results;
        var totalWeight = results.Sum(Weight);
        var meanHome = results.Sum(r => Weight(r) * r.HomeGoals) / totalWeight;
        var meanAway = results.Sum(r => Weight(r) * r.AwayGoals) / totalWeight;

        var ids = league.Clubs.Select(c => c.Id).ToList();
        var attack = ids.ToDictionary(id => id, _ => 1.0);
        var defense = ids.ToDictionary(id => id, _ => 1.0);

        for (var iteration = 0; iteration < FitIterations; iteration++)
        {
            var newAttack = new Dictionary<string, double>();
            var newDefense = new Dictionary<string, double>();
            foreach (var id in ids)
            {
                double scored = 0, expectedScored = 0, conceded = 0, expectedConceded = 0;
                foreach (var r in results)
                {
                    var w = Weight(r);
                    if (r.HomeClubId == id)
                    {
                        scored += w * r.HomeGoals;
                        expectedScored += w * meanHome * defense[r.AwayClubId];
                        conceded += w * r.AwayGoals;
                        expectedConceded += w * meanAway * attack[r.AwayClubId];
                    }
                    else if (r.AwayClubId == id)
                    {
                        scored += w * r.AwayGoals;
                        expectedScored += w * meanAway * defense[r.HomeClubId];
                        conceded += w * r.HomeGoals;
                        expectedConceded += w * meanHome * attack[r.HomeClubId];
                    }
                }

                newAttack[id] = expectedScored > 0 ? scored / expectedScored : 1;
                newDefense[id] = expectedConceded > 0 ? conceded / expectedConceded : 1;
            }

            var attackMean = newAttack.Values.Average();
            var defenseMean = newDefense.Values.Average();
            foreach (var id in ids)
            {
                attack[id] = newAttack[id] / attackMean;
                defense[id] = newDefense[id] / defenseMean;
            }
        }

        var latest = seasons[^1];
        var ratings = ids.ToDictionary(id => id, id =>
        {
            var games = results.Where(r => r.Season == latest && (r.HomeClubId == id || r.AwayClubId == id)).ToList();
            var gf = games.Sum(r => r.HomeClubId == id ? r.HomeGoals : r.AwayGoals);
            var ga = games.Sum(r => r.HomeClubId == id ? r.AwayGoals : r.HomeGoals);
            var n = Math.Max(1, games.Count);
            return new TeamRating(id, Math.Round(attack[id], 3), Math.Round(defense[id], 3), Math.Round((double)gf / n, 2), Math.Round((double)ga / n, 2));
        }, StringComparer.Ordinal);

        return (ratings, meanHome, meanAway);
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return i;
            }
        }

        return 0;
    }
}
