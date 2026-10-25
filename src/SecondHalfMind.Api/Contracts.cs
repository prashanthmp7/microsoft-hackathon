using System.Text.RegularExpressions;
using SecondHalfMind.Agents;
using SecondHalfMind.Agents.Briefing;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Api;

// ---- Requests (everything a client can send). Free text is limited to a validated nickname. ----

public sealed record ProfileDto(string? Mode, string? Language, string? FavoriteClubId, string? FocusPlayerId, string? FocusMetric);

public sealed record CreateMatchRequest(string HomeClubId, string AwayClubId, int? Seed);

public sealed record StartMatchRequest(double? Speed);

public sealed record PickRequest(string FanId, string Nickname, string Outcome, int HomeGoals, int AwayGoals);

public sealed record AnswerRequest(string FanId, string Nickname, string QuestionId, bool AnswerYes);

// ---- Views ----

public sealed record ClubView(string Id, string Name, string ShortName, string PrimaryColor, string SecondaryColor);

public sealed record PlayerView(string Id, string Name, int ShirtNumber, string Position);

public sealed record GoalView(int Minute, string Team, string PlayerId, string PlayerName);

public sealed record WinProbabilityPoint(int Minute, double Home, double Draw, double Away);

public sealed record MatchSummary(string Id, ClubView Home, ClubView Away, string Status, int Minute, int HomeGoals, int AwayGoals);

public sealed record MatchView(
    string Id,
    string Status,
    string Phase,
    int Minute,
    int ClockSeconds,
    ClubView Home,
    ClubView Away,
    TeamStats HomeStats,
    TeamStats AwayStats,
    double HomePossession,
    IReadOnlyList<GoalView> Goals,
    WinProbabilityPoint? WinProbability);

public static class Views
{
    public static ClubView Club(Club c) => new(c.Id, c.Name, c.ShortName, c.PrimaryColor, c.SecondaryColor);

    public static PlayerView Player(Player p) => new(p.Id, p.Name, p.ShirtNumber, p.Position.ToString());

    public static MatchView Match(MatchInfo info, IMatchReadModel matches, ILeagueReadModel league, IInsightReadModel insights)
    {
        var snapshot = matches.GetSnapshot(info.Id);
        var home = league.GetClub(info.HomeClubId)!;
        var away = league.GetClub(info.AwayClubId)!;
        var wp = insights.GetInsights(info.Id, 0, 1000).LastOrDefault(i => i.Kind == InsightKind.WinProbability);
        return new MatchView(
            info.Id,
            matches.GetStatus(info.Id).ToString(),
            (snapshot?.Phase ?? MatchPhase.PreMatch).ToString(),
            snapshot?.Minute ?? 0,
            snapshot?.ClockSeconds ?? 0,
            Club(home),
            Club(away),
            snapshot?.Home ?? TeamStats.Empty,
            snapshot?.Away ?? TeamStats.Empty,
            snapshot?.HomePossessionShare ?? 0.5,
            (snapshot?.Goals ?? []).Select(g => new GoalView(g.Minute, g.Team.ToString(), g.PlayerId, league.GetPlayer(g.PlayerId)?.Name ?? string.Empty)).ToList(),
            wp is null ? null : WinPoint(wp));
    }

    public static WinProbabilityPoint WinPoint(Insight i) =>
        new(i.Minute, i.Facts.GetValueOrDefault("home"), i.Facts.GetValueOrDefault("draw"), i.Facts.GetValueOrDefault("away"));
}

/// <summary>Input validation at the trust boundary. Everything is allow-listed.</summary>
public static partial class Validation
{
    private static readonly string[] BlockedNicknameFragments =
    [
        "admin", "moderator", "official", "system", "bet", "casino",
    ];

    public static bool TryProfile(ProfileDto? dto, MatchInfo match, ILeagueReadModel league, out ViewerProfile profile, out string? error)
    {
        profile = ViewerProfile.Default;
        error = null;
        if (dto is null)
        {
            return true;
        }

        var mode = ViewerMode.Casual;
        if (dto.Mode is not null && !Enum.TryParse(dto.Mode, ignoreCase: true, out mode))
        {
            error = "Unknown mode.";
            return false;
        }

        var language = dto.Language ?? "en";
        if (!PersonalizationAgent.SupportedLanguages.ContainsKey(language))
        {
            error = "Unsupported language.";
            return false;
        }

        if (dto.FavoriteClubId is not null && league.GetClub(dto.FavoriteClubId) is null)
        {
            error = "Unknown club.";
            return false;
        }

        if (dto.FocusPlayerId is not null)
        {
            var player = league.GetPlayer(dto.FocusPlayerId);
            if (player is null || (player.ClubId != match.HomeClubId && player.ClubId != match.AwayClubId))
            {
                error = "Focus player must be in this match.";
                return false;
            }
        }

        if (dto.FocusMetric is not null && !PersonalizationAgent.FocusMetrics.Contains(dto.FocusMetric))
        {
            error = "Unknown metric.";
            return false;
        }

        profile = new ViewerProfile(mode, language, dto.FavoriteClubId, dto.FocusPlayerId, dto.FocusMetric);
        return true;
    }

    public static bool TryHighlightCount(int? requested, out int count, out string? error)
    {
        count = requested ?? HighlightReel.DefaultCount;
        error = null;
        if (count is < 1 or > HighlightReel.MaxCount)
        {
            error = $"max must be between 1 and {HighlightReel.MaxCount}.";
            return false;
        }

        return true;
    }

    public static bool IsValidFanId(string? fanId) => Guid.TryParse(fanId, out _);

    public static bool IsValidNickname(string? nickname) =>
        nickname is not null
        && NicknamePattern().IsMatch(nickname)
        && !BlockedNicknameFragments.Any(f => nickname.Contains(f, StringComparison.OrdinalIgnoreCase));

    public static bool TryPick(PickRequest request, string matchId, out FanPick pick, out string? error)
    {
        pick = null!;
        error = null;
        if (!IsValidFanId(request.FanId) || !IsValidNickname(request.Nickname))
        {
            error = "Invalid fan id or nickname (2-20 letters, numbers, spaces, - or _).";
            return false;
        }

        if (request.HomeGoals is < 0 or > 9 || request.AwayGoals is < 0 or > 9)
        {
            error = "Goals must be between 0 and 9.";
            return false;
        }

        if (!Enum.TryParse<MatchOutcome>(request.Outcome, ignoreCase: true, out var outcome))
        {
            error = "Outcome must be Home, Draw or Away.";
            return false;
        }

        var implied = request.HomeGoals > request.AwayGoals ? MatchOutcome.Home
            : request.HomeGoals == request.AwayGoals ? MatchOutcome.Draw
            : MatchOutcome.Away;
        if (implied != outcome)
        {
            error = "Score and outcome don't match.";
            return false;
        }

        pick = new FanPick(request.FanId, request.Nickname.Trim(), matchId, new ScorePrediction(outcome, request.HomeGoals, request.AwayGoals), DateTimeOffset.UtcNow);
        return true;
    }

    [GeneratedRegex(@"^[A-Za-z0-9 _\-]{2,20}$", RegexOptions.None, 100)]
    private static partial Regex NicknamePattern();
}
