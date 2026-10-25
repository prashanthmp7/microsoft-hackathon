using System.Collections.Concurrent;
using SecondHalfMind.Agents.Models;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents.Briefing;

/// <summary>Builds and caches the pre-match briefing: model call, AI pundit call and storylines.</summary>
public sealed class BriefingService
{
    private readonly ILeagueReadModel _league;
    private readonly IMatchReadModel _matches;
    private readonly PreMatchModel _model;
    private readonly ConcurrentDictionary<string, PreMatchBriefing> _cache = new(StringComparer.Ordinal);

    public BriefingService(ILeagueReadModel league, IMatchReadModel matches, PreMatchModel model)
    {
        _league = league;
        _matches = matches;
        _model = model;
    }

    public PreMatchModel Model => _model;

    public PreMatchBriefing? Get(string matchId)
    {
        if (_cache.TryGetValue(matchId, out var cached))
        {
            return cached;
        }

        var match = _matches.GetMatch(matchId);
        var home = match is null ? null : _league.GetClub(match.HomeClubId);
        var away = match is null ? null : _league.GetClub(match.AwayClubId);
        if (match is null || home is null || away is null)
        {
            return null;
        }

        var (lh, la) = _model.ExpectedGoals(home.Id, away.Id);
        var briefing = new PreMatchBriefing(
            match.Id,
            _model.Predict(home, away),
            PunditPersona.Predict(home, away, _model),
            Storylines.For(home, away, _model),
            lh,
            la);
        return _cache.GetOrAdd(matchId, briefing);
    }
}

/// <summary>
/// The AI pundit: a deliberately human-style persona that trusts recent form, home crowds and
/// head-to-head history more than underlying numbers. It gives fans a second opinion to beat.
/// </summary>
public static class PunditPersona
{
    public static PreMatchCall Predict(Club home, Club away, PreMatchModel model)
    {
        var formHome = model.Form(home.Id);
        var formAway = model.Form(away.Id);
        var h2h = model.HeadToHead(home.Id, away.Id);
        var h2hBalance = h2h.Played == 0 ? 0 : (double)(h2h.HomeWins - h2h.AwayWins) / h2h.Played;
        var lean = (formHome.PointsPerGame - formAway.PointsPerGame) + 0.3 + 0.3 * h2hBalance;

        ScorePrediction call;
        string reason;
        if (Math.Abs(lean) < 0.45)
        {
            call = new ScorePrediction(MatchOutcome.Draw, 1, 1);
            reason = Fmt.S($"Too close to call. {home.ShortName} form {formHome.Sequence}, {away.ShortName} form {formAway.Sequence}. I'll take a share of the points.");
        }
        else
        {
            var homeWins = lean > 0;
            var winner = homeWins ? home : away;
            var strong = Math.Abs(lean) > 1.2;
            var (w, l) = strong ? (2, 0) : (2, 1);
            call = homeWins ? new ScorePrediction(MatchOutcome.Home, w, l) : new ScorePrediction(MatchOutcome.Away, l, w);
            var winnerForm = homeWins ? formHome : formAway;
            reason = Fmt.S($"{winner.Name} are in better shape: {winnerForm.Sequence} in their last {winnerForm.Games}, {winnerForm.Points} points. Form wins matches.");
        }

        var factors = new List<PredictionFactor>
        {
            new("Recent form", Fmt.S($"{home.ShortName} {formHome.Sequence} ({formHome.Points} pts), {away.ShortName} {formAway.Sequence} ({formAway.Points} pts)"), Math.Round(formHome.PointsPerGame - formAway.PointsPerGame, 2)),
            new("Home crowd", "The pundit always gives the home side a nudge", 0.3),
            new("Head to head", Fmt.S($"{h2h.Played} meetings: {h2h.HomeWins} {home.ShortName} wins, {h2h.Draws} draws, {h2h.AwayWins} {away.ShortName} wins"), Math.Round(h2hBalance, 2)),
        };

        return new PreMatchCall("AI pundit", call, null, factors, reason);
    }
}

public static class Storylines
{
    public static IReadOnlyList<Storyline> For(Club home, Club away, PreMatchModel model)
    {
        var hr = model.Rating(home.Id);
        var ar = model.Rating(away.Id);
        var stories = new List<Storyline>();

        // 1. The sharpest attack against the other side's defence.
        var (attackClub, attackRating, defenceClub, defenceRating) = hr.Attack / ar.Defense >= ar.Attack / hr.Defense
            ? (home, hr, away, ar)
            : (away, ar, home, hr);
        stories.Add(new Storyline(
            "attack-vs-defence",
            $"{attackClub.ShortName} attack vs {defenceClub.ShortName} defence",
            Fmt.S($"{attackClub.Name} scored {attackRating.GoalsForPerGame:0.00} a game last season; {defenceClub.Name} conceded {defenceRating.GoalsAgainstPerGame:0.00}."),
            "Shots on target before the half-hour"));

        // 2. Form.
        var fh = model.Form(home.Id);
        var fa = model.Form(away.Id);
        var (inForm, form, other) = fh.Points >= fa.Points ? (home, fh, away) : (away, fa, home);
        stories.Add(new Storyline(
            "form",
            $"{inForm.ShortName} arrive in form",
            Fmt.S($"{form.Points} points from their last {form.Games} ({form.Sequence})."),
            $"Whether {other.Name} can slow the tempo and break their rhythm"));

        // 3. The finisher to watch.
        var finisher = home.Squad.Take(11).Concat(away.Squad.Take(11))
            .Where(p => p.Position == Position.Forward)
            .OrderByDescending(p => p.Attributes.Finishing)
            .First();
        var finisherClub = finisher.ClubId == home.Id ? home : away;
        stories.Add(new Storyline(
            "finisher",
            $"{finisher.Name} is the one to watch",
            $"{Fmt.Possessive(finisherClub.Name)} sharpest finisher in the starting line-up.",
            "Shot speed and shots from inside the box"));

        return stories;
    }
}
