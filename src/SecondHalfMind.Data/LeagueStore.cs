using System.Text.Json;
using SecondHalfMind.Domain;
using SecondHalfMind.Simulation;

namespace SecondHalfMind.Data;

/// <summary>The synthetic league, its generated history and the calibration priors. Read-only after startup.</summary>
public sealed class LeagueStore : ILeagueReadModel
{
    private readonly Dictionary<string, Club> _clubs;
    private readonly Dictionary<string, Player> _players;

    public LeagueStore(CalibrationProfile calibration, int leagueSeed = 2026, int historySeed = 7)
    {
        Calibration = calibration;
        Clubs = LeagueFactory.Create(calibration, leagueSeed);
        History = HistoryGenerator.Generate(Clubs, calibration, seasons: 3, seed: historySeed);
        _clubs = Clubs.ToDictionary(c => c.Id, StringComparer.Ordinal);
        _players = Clubs.SelectMany(c => c.Squad).ToDictionary(p => p.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<Club> Clubs { get; }

    public LeagueHistory History { get; }

    public CalibrationProfile Calibration { get; }

    public Club? GetClub(string clubId) => _clubs.GetValueOrDefault(clubId);

    public Player? GetPlayer(string playerId) => _players.GetValueOrDefault(playerId);

    /// <summary>Loads calibration.json if present, otherwise falls back to the built-in default priors.</summary>
    public static CalibrationProfile LoadCalibration(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return CalibrationProfile.Defaults;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<CalibrationProfile>(json, JsonDefaults.Options)
            ?? throw new InvalidDataException($"Calibration file '{path}' is empty.");
    }
}

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
