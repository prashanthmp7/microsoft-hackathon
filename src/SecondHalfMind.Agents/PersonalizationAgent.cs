using System.Collections.Concurrent;
using SecondHalfMind.Agents.Ai;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents;

/// <summary>
/// Personalise and render stages: decides whether a viewer sees an insight, which voice they get
/// (analyst or casual), whether it concerns their club or focus player, and in which language,
/// and emits it as a timed, machine-readable overlay.
/// </summary>
public sealed class PersonalizationAgent
{
    /// <summary>Languages offered in the UI. Anything else is rejected at the API boundary.</summary>
    public static readonly IReadOnlyDictionary<string, string> SupportedLanguages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["en"] = "English",
        ["es"] = "Español",
        ["fr"] = "Français",
        ["pt"] = "Português",
        ["de"] = "Deutsch",
        ["it"] = "Italiano",
        ["hi"] = "हिन्दी",
        ["ta"] = "தமிழ்",
        ["ar"] = "العربية",
        ["ja"] = "日本語",
        ["zh-Hans"] = "中文",
    };

    public static readonly IReadOnlySet<string> FocusMetrics = new HashSet<string>(StringComparer.Ordinal)
    {
        "passDifficulty", "shotSpeed", "speed", "distance", "control", "pressure",
    };

    private const int TranslationCacheLimit = 20_000;
    private readonly ITranslator _translator;
    private readonly ConcurrentDictionary<string, string> _translations = new(StringComparer.Ordinal);

    public PersonalizationAgent(ITranslator translator)
    {
        _translator = translator;
    }

    public static bool IsVisible(Insight insight, ViewerProfile profile, Side? favoriteSide = null)
    {
        if (insight.Kind == InsightKind.WinProbability)
        {
            return false; // delivered as a data series, not an overlay
        }

        if (insight.Kind is InsightKind.KeyMoment or InsightKind.Recap or InsightKind.PredictionOpened or InsightKind.PredictionResolved)
        {
            return true;
        }

        if (profile.FocusPlayerId is not null && insight.PlayerId == profile.FocusPlayerId)
        {
            return true;
        }

        if (profile.FocusMetric is not null && insight.Metric == profile.FocusMetric && insight.Priority >= 2)
        {
            return true;
        }

        var forClub = favoriteSide is not null && insight.Team == favoriteSide;
        return profile.Mode switch
        {
            ViewerMode.Analyst => insight.Priority >= 2,
            _ => insight.Priority >= 3 || (forClub && insight.Priority >= 2),
        };
    }

    public async Task<Overlay?> ShapeAsync(Insight insight, ViewerProfile profile, MatchInfo match, CancellationToken cancellationToken)
    {
        Side? favoriteSide = profile.FavoriteClubId == match.HomeClubId ? Side.Home
            : profile.FavoriteClubId == match.AwayClubId ? Side.Away
            : null;

        if (!IsVisible(insight, profile, favoriteSide))
        {
            return null;
        }

        var text = profile.Mode == ViewerMode.Analyst ? insight.Analyst : insight.Casual;
        var title = text.Headline;
        var body = text.Body;
        var translated = false;

        if (profile.Language != "en" && _translator.IsConfigured)
        {
            var result = await TranslateAsync([title, body], profile.Language, cancellationToken);
            if (result is { Count: 2 })
            {
                (title, body, translated) = (result[0], result[1], true);
            }
        }

        var template = insight.Kind switch
        {
            InsightKind.KeyMoment => "lower-third",
            InsightKind.Recap => "recap-panel",
            InsightKind.PredictionOpened or InsightKind.PredictionResolved => "prediction-card",
            InsightKind.PlayerAlert or InsightKind.Milestone => "player-tag",
            _ => "stat-card",
        };

        var duration = insight.Priority switch
        {
            5 => 9000,
            4 => 7000,
            3 => 6000,
            _ => 4500,
        };

        return new Overlay(
            Id: $"{insight.Id}:{profile.Mode.ToString().ToLowerInvariant()}:{profile.Language}",
            MatchId: insight.MatchId,
            InsightId: insight.Id,
            ClockSeconds: insight.ClockSeconds,
            Minute: insight.Minute,
            Template: template,
            DurationMs: duration,
            Priority: insight.Priority,
            Mode: profile.Mode.ToString().ToLowerInvariant(),
            Language: translated ? profile.Language : "en",
            Translated: translated,
            Title: title,
            Text: body,
            Team: insight.Team,
            PlayerId: insight.PlayerId,
            Metric: insight.Metric,
            Kind: insight.Kind.ToString(),
            Data: insight.Facts,
            Tags: insight.Tags,
            ForYourClub: favoriteSide is not null && insight.Team == favoriteSide,
            ForYourPlayer: profile.FocusPlayerId is not null && insight.PlayerId == profile.FocusPlayerId);
    }

    private async Task<IReadOnlyList<string>?> TranslateAsync(IReadOnlyList<string> texts, string language, CancellationToken ct)
    {
        var results = new string[texts.Count];
        var missing = new List<int>();
        for (var i = 0; i < texts.Count; i++)
        {
            if (_translations.TryGetValue($"{language}|{texts[i]}", out var cached))
            {
                results[i] = cached;
            }
            else
            {
                missing.Add(i);
            }
        }

        if (missing.Count > 0)
        {
            var translated = await _translator.TranslateAsync(missing.Select(i => texts[i]).ToList(), language, ct);
            if (translated is null || translated.Count != missing.Count)
            {
                return null;
            }

            for (var j = 0; j < missing.Count; j++)
            {
                results[missing[j]] = translated[j];
                if (_translations.Count < TranslationCacheLimit)
                {
                    _translations[$"{language}|{texts[missing[j]]}"] = translated[j];
                }
            }
        }

        return results;
    }
}
