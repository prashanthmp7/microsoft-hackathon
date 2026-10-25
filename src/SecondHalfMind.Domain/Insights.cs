using System.Collections.Frozen;

namespace SecondHalfMind.Domain;

public enum InsightKind
{
    Stat,
    PlayerAlert,
    KeyMoment,
    Milestone,
    WinProbability,
    PredictionOpened,
    PredictionResolved,
    Recap,
}

public sealed record TextVariant(string Headline, string Body);

/// <summary>
/// Agent output. Insights live in their own append-only store; they never alter match data.
/// Facts are the verified numbers the text is allowed to use; Tags carry identifiers.
/// </summary>
public sealed record Insight
{
    public required string Id { get; init; }
    public required string MatchId { get; init; }
    public required long SourceSequence { get; init; }
    public required int ClockSeconds { get; init; }
    public required InsightKind Kind { get; init; }
    public required string Agent { get; init; }

    /// <summary>1 (background) .. 5 (must show).</summary>
    public required int Priority { get; init; }

    public Side? Team { get; init; }
    public string? PlayerId { get; init; }
    public string? Metric { get; init; }
    public required TextVariant Analyst { get; init; }
    public required TextVariant Casual { get; init; }

    // Frozen copies: insights are shared between agents (upstream handoff) and the store, so a
    // holder must never be able to cast these back to a mutable dictionary and change them.
    private readonly FrozenDictionary<string, double> _facts = FrozenDictionary<string, double>.Empty;
    private readonly FrozenDictionary<string, string> _tags = FrozenDictionary<string, string>.Empty;

    public IReadOnlyDictionary<string, double> Facts
    {
        get => _facts;
        init => _facts = value.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, string> Tags
    {
        get => _tags;
        init => _tags = value.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>"template" or "llm". LLM text is only used after passing the output guard.</summary>
    public required string GeneratedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Match minute as shown on screen (45 at half-time).</summary>
    public required int Minute { get; init; }
}

public enum ViewerMode { Casual, Analyst }

/// <summary>A viewer's personalisation choices. Every field is an enum or an identifier; no free text.</summary>
public sealed record ViewerProfile(
    ViewerMode Mode,
    string Language,
    string? FavoriteClubId,
    string? FocusPlayerId,
    string? FocusMetric)
{
    public static ViewerProfile Default { get; } = new(ViewerMode.Casual, "en", null, null, null);

    public string CacheKey => $"{Mode}|{Language}|{FavoriteClubId}|{FocusPlayerId}|{FocusMetric}";
}

/// <summary>
/// Timed, machine-readable overlay instruction for a broadcast graphics renderer or a fan client.
/// </summary>
public sealed record Overlay(
    string Id,
    string MatchId,
    string InsightId,
    int ClockSeconds,
    int Minute,
    string Template,
    int DurationMs,
    int Priority,
    string Mode,
    string Language,
    bool Translated,
    string Title,
    string Text,
    Side? Team,
    string? PlayerId,
    string? Metric,
    string Kind,
    IReadOnlyDictionary<string, double> Data,
    IReadOnlyDictionary<string, string> Tags,
    bool ForYourClub,
    bool ForYourPlayer);
