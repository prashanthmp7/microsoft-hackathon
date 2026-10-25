using System.Text.Json;
using SecondHalfMind.Data;
using SecondHalfMind.Domain;
using SecondHalfMind.Simulation;
using SecondHalfMind.Tools;

// Usage:
//   calibrate [--seasons 2324,2425,2526] [--file a.csv --file b.csv] [--out src/SecondHalfMind.Api/data/calibration.json]
//   generate  [--matches 10] [--seed 42] [--out datasets] [--calibration src/SecondHalfMind.Api/data/calibration.json]

var command = args.FirstOrDefault() ?? "help";
var options = ParseOptions(args.Skip(1).ToArray());

return command switch
{
    "calibrate" => await CalibrateAsync(options),
    "generate" => Generate(options),
    _ => Help(),
};

static async Task<int> CalibrateAsync(Dictionary<string, List<string>> options)
{
    var output = Single(options, "out") ?? Path.Combine("src", "SecondHalfMind.Api", "data", "calibration.json");
    var rows = new List<Calibrator.ResultRow>();
    string source;

    if (options.TryGetValue("file", out var files))
    {
        foreach (var file in files)
        {
            rows.AddRange(Calibrator.ParseCsv(Path.GetFileNameWithoutExtension(file), await File.ReadAllTextAsync(file)));
        }

        source = "local files (historical English top-flight results)";
    }
    else
    {
        // The last three completed seasons as of the 2026-27 season. Data from football-data.co.uk,
        // read in memory and reduced to aggregates; raw files are not stored.
        var seasons = (Single(options, "seasons") ?? "2324,2425,2526").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SecondHalfMind-Calibrator/1.0");
        foreach (var season in seasons)
        {
            var url = $"https://www.football-data.co.uk/mmz4281/{season}/E0.csv";
            Console.WriteLine($"Fetching {url}");
            var csv = await http.GetStringAsync(new Uri(url));
            var parsed = Calibrator.ParseCsv(season, csv);
            Console.WriteLine($"  {parsed.Count} results");
            rows.AddRange(parsed);
        }

        source = $"football-data.co.uk E0 results, seasons {string.Join(", ", seasons)} (aggregates only)";
    }

    var profile = Calibrator.Compute(rows, source);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(profile, JsonDefaults.Options));

    Console.WriteLine();
    Console.WriteLine($"Calibrated from {profile.MatchCount} matches -> {output}");
    Console.WriteLine($"  Home/Draw/Away  {profile.HomeWinRate:P1} / {profile.DrawRate:P1} / {profile.AwayWinRate:P1}");
    Console.WriteLine($"  Goals per game  home {profile.MeanHomeGoals:0.00}, away {profile.MeanAwayGoals:0.00}");
    Console.WriteLine($"  Shots per team  {profile.ShotsPerTeamMean:0.0} ({profile.ShotsOnTargetRate:P0} on target)");
    Console.WriteLine($"  Strength spread attack {profile.AttackLogStd:0.000}, defence {profile.DefenseLogStd:0.000}");
    Console.WriteLine("No club names, player names or match rows were written.");
    return 0;
}

static int Generate(Dictionary<string, List<string>> options)
{
    var output = Single(options, "out") ?? "datasets";
    var matches = int.Parse(Single(options, "matches") ?? "10", System.Globalization.CultureInfo.InvariantCulture);
    var seed = int.Parse(Single(options, "seed") ?? "42", System.Globalization.CultureInfo.InvariantCulture);
    var calibration = LeagueStore.LoadCalibration(Single(options, "calibration") ?? Path.Combine("src", "SecondHalfMind.Api", "data", "calibration.json"));
    var league = new LeagueStore(calibration);
    Directory.CreateDirectory(output);

    File.WriteAllText(Path.Combine(output, "league.json"), JsonSerializer.Serialize(league.Clubs, JsonDefaults.Options));

    using (var history = new StreamWriter(Path.Combine(output, "history.csv")))
    {
        history.WriteLine("season,round,home_club_id,away_club_id,home_goals,away_goals");
        foreach (var r in league.History.Results)
        {
            history.WriteLine($"{r.Season},{r.Round},{r.HomeClubId},{r.AwayClubId},{r.HomeGoals},{r.AwayGoals}");
        }
    }

    var random = new Random(seed);
    var compact = new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = false };
    for (var i = 0; i < matches; i++)
    {
        var home = league.Clubs[random.Next(league.Clubs.Count)];
        Club away;
        do
        {
            away = league.Clubs[random.Next(league.Clubs.Count)];
        }
        while (away.Id == home.Id);

        var info = new MatchInfo($"synthetic-{i + 1:000}-{home.ShortName.ToLowerInvariant()}-{away.ShortName.ToLowerInvariant()}", home.Id, away.Id, random.Next(), DateTimeOffset.UtcNow);
        var events = MatchSimulator.Simulate(info, home, away, calibration);
        File.WriteAllLines(Path.Combine(output, $"{info.Id}.jsonl"), events.Select(e => JsonSerializer.Serialize(e, compact)));
    }

    Console.WriteLine($"Wrote league.json, history.csv and {matches} match event streams to {output} (calibration: {calibration.Source})");
    return 0;
}

static int Help()
{
    Console.WriteLine("""
        Second Half Mind tools
          calibrate [--seasons 2324,2425,2526] [--file results.csv ...] [--out path]
              Download historical top-flight results and reduce them to anonymous priors.
          generate [--matches 10] [--seed 42] [--out datasets] [--calibration path]
              Export the synthetic league, its history and match event streams (JSON Lines).
        """);
    return 0;
}

static Dictionary<string, List<string>> ParseOptions(string[] args)
{
    var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            var key = args[i][2..];
            if (!result.TryGetValue(key, out var list))
            {
                result[key] = list = [];
            }

            list.Add(args[++i]);
        }
    }

    return result;
}

static string? Single(Dictionary<string, List<string>> options, string key) =>
    options.TryGetValue(key, out var values) ? values[^1] : null;
