namespace SecondHalfMind.Domain;

public enum Side { Home, Away }

public enum Position { Goalkeeper, Defender, Midfielder, Forward }

/// <summary>Player ratings on a 0..1 scale. All players are synthetic.</summary>
public sealed record PlayerAttributes(double Passing, double Finishing, double Pace, double Stamina, double Defending);

public sealed record Player(
    string Id,
    string Name,
    int ShirtNumber,
    Position Position,
    string ClubId,
    PlayerAttributes Attributes);

/// <summary>A fictional club. Attack and Defense are multiplicative strengths around 1.0 (lower defense = better).</summary>
public sealed record Club(
    string Id,
    string Name,
    string ShortName,
    string PrimaryColor,
    string SecondaryColor,
    double Attack,
    double Defense,
    IReadOnlyList<Player> Squad);

/// <summary>A completed fixture in the synthetic league history used to train the prediction model.</summary>
public sealed record HistoricalResult(string Season, int Round, string HomeClubId, string AwayClubId, int HomeGoals, int AwayGoals);

public sealed record LeagueHistory(IReadOnlyList<string> Seasons, IReadOnlyList<HistoricalResult> Results);
