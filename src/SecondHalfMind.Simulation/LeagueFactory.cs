using SecondHalfMind.Domain;

namespace SecondHalfMind.Simulation;

/// <summary>
/// Builds a fictional 20-club league. Club strengths are drawn from the calibrated spread of real
/// top-flight attack and defence strengths, so the synthetic league behaves like the real one
/// without containing any real club or player.
/// </summary>
public static class LeagueFactory
{
    private static readonly (string Id, string Name, string Short, string Primary, string Secondary)[] ClubSeeds =
    [
        ("ashbury", "Ashbury Rovers", "ASH", "#1D4ED8", "#FFFFFF"),
        ("kingsmere", "Kingsmere Athletic", "KGM", "#B91C1C", "#FDE68A"),
        ("northvale", "Northvale City", "NVC", "#0EA5E9", "#0F172A"),
        ("redcastle", "Redcastle United", "RCU", "#DC2626", "#111827"),
        ("harbourside", "Harbourside FC", "HBS", "#0F766E", "#F8FAFC"),
        ("westbrook", "Westbrook Albion", "WBA", "#4338CA", "#F1F5F9"),
        ("stonebridge", "Stonebridge Town", "STB", "#57534E", "#FACC15"),
        ("elmfield", "Elmfield Wanderers", "ELM", "#15803D", "#FFFFFF"),
        ("ravenport", "Ravenport FC", "RVP", "#111827", "#F97316"),
        ("brookhaven", "Brookhaven Athletic", "BRK", "#7C3AED", "#FFFFFF"),
        ("ironvale", "Ironvale United", "IRV", "#374151", "#E11D48"),
        ("oakridge", "Oakridge City", "OAK", "#65A30D", "#1E293B"),
        ("saltmarsh", "Saltmarsh Rovers", "SLT", "#0369A1", "#FDE047"),
        ("highcliffe", "Highcliffe Town", "HCT", "#9D174D", "#E0F2FE"),
        ("larkspur", "Larkspur FC", "LRK", "#6D28D9", "#FCD34D"),
        ("thornbury", "Thornbury Athletic", "THB", "#B45309", "#0F172A"),
        ("copperfield", "Copperfield United", "CPF", "#C2410C", "#FFFFFF"),
        ("fairhaven", "Fairhaven City", "FHC", "#0284C7", "#FFFFFF"),
        ("greywater", "Greywater Rovers", "GRW", "#475569", "#38BDF8"),
        ("millbrook", "Millbrook Albion", "MLB", "#047857", "#FBBF24"),
    ];

    private static readonly string[] FirstNames =
    [
        "Leo", "Mateo", "Kai", "Noah", "Theo", "Ilan", "Rafa", "Sami", "Jonah", "Elias", "Tomas", "Arlo",
        "Nico", "Ezra", "Milo", "Dario", "Luca", "Femi", "Kofi", "Joao", "Ren", "Aki", "Omar", "Yusuf",
        "Ivo", "Bram", "Sven", "Oscar", "Tiago", "Emil", "Hugo", "Aren", "Caio", "Dmitri", "Ellis", "Felix",
    ];

    private static readonly string[] SurnameStarts =
    [
        "Ash", "Bren", "Cal", "Dar", "El", "Fen", "Gar", "Hal", "Iver", "Jor", "Kel", "Lan", "Mor",
        "Nor", "Ost", "Pel", "Quin", "Rav", "Sel", "Tor", "Ul", "Ven", "Wes", "Yar", "Zel",
    ];

    private static readonly string[] SurnameEnds =
    [
        "by", "ford", "wick", "ton", "ley", "mere", "dale", "holt", "croft", "wood", "ard", "ell", "ane", "ock",
    ];

    // 3 GK, 8 DF, 7 MF, 5 FW.
    private static readonly Position[] SquadShape =
    [
        Position.Goalkeeper, Position.Defender, Position.Defender, Position.Defender, Position.Defender,
        Position.Midfielder, Position.Midfielder, Position.Midfielder, Position.Forward, Position.Forward,
        Position.Forward,
        Position.Goalkeeper, Position.Defender, Position.Defender, Position.Midfielder, Position.Midfielder,
        Position.Forward, Position.Defender, Position.Midfielder, Position.Forward, Position.Defender,
        Position.Midfielder, Position.Goalkeeper,
    ];

    public static IReadOnlyList<Club> Create(CalibrationProfile calibration, int seed = 2026)
    {
        var random = new Random(seed);
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var clubs = new List<Club>(ClubSeeds.Length);

        foreach (var (id, name, shortName, primary, secondary) in ClubSeeds)
        {
            // Log-normal strengths centred on 1.0, with spread taken from calibration.
            var quality = random.NextGaussian();
            var attack = Math.Exp(calibration.AttackLogStd * (0.8 * quality + 0.6 * random.NextGaussian()));
            var defense = Math.Exp(-calibration.DefenseLogStd * (0.8 * quality + 0.6 * random.NextGaussian()));
            var squad = CreateSquad(random, id, quality, usedNames);
            clubs.Add(new Club(id, name, shortName, primary, secondary, Math.Round(attack, 3), Math.Round(defense, 3), squad));
        }

        return clubs;
    }

    private static List<Player> CreateSquad(Random random, string clubId, double quality, HashSet<string> usedNames)
    {
        var squad = new List<Player>(SquadShape.Length);
        var baseline = Math.Clamp(0.62 + 0.08 * quality, 0.4, 0.85);

        for (var i = 0; i < SquadShape.Length; i++)
        {
            var position = SquadShape[i];
            var shirt = i + 1;
            var starterBoost = i < 11 ? 0.05 : -0.02;
            double Rating(double positionBias) =>
                Math.Round(RandomExtensions.Clamp01(baseline + starterBoost + positionBias + random.NextGaussian(0, 0.07)), 2);

            var attributes = position switch
            {
                Position.Goalkeeper => new PlayerAttributes(Rating(-0.1), Rating(-0.5), Rating(-0.2), Rating(0), Rating(0.05)),
                Position.Defender => new PlayerAttributes(Rating(-0.02), Rating(-0.25), Rating(0), Rating(0.05), Rating(0.12)),
                Position.Midfielder => new PlayerAttributes(Rating(0.1), Rating(-0.05), Rating(0), Rating(0.1), Rating(0)),
                _ => new PlayerAttributes(Rating(0), Rating(0.12), Rating(0.1), Rating(0), Rating(-0.2)),
            };

            squad.Add(new Player($"{clubId}-{shirt:00}", UniqueName(random, usedNames), shirt, position, clubId, attributes));
        }

        return squad;
    }

    private static string UniqueName(Random random, HashSet<string> usedNames)
    {
        while (true)
        {
            var name = $"{random.Pick(FirstNames)} {random.Pick(SurnameStarts)}{random.Pick(SurnameEnds)}";
            if (usedNames.Add(name))
            {
                return name;
            }
        }
    }
}
