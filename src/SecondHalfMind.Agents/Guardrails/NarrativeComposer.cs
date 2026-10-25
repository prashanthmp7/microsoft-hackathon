using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecondHalfMind.Agents.Ai;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents.Guardrails;

/// <summary>
/// Optional language-model voice for the biggest moments. The model receives only verified facts
/// and the template text (never viewer input), has no tools, and its output must pass the
/// output guard and content safety, otherwise the deterministic template is kept.
/// </summary>
public sealed class NarrativeComposer
{
    internal const string SystemPrompt = """
        You write short on-screen football commentary for a free-to-play fan app.
        Rules:
        - Use ONLY the facts and names in the JSON you are given. Do not invent players, numbers, events or quotes.
        - Every number you write must appear in "facts" (probabilities may be written as percentages).
        - Never mention betting, odds, gambling or money.
        - Plain text only: no links, hashtags, emojis, markup or brackets.
        - "analyst": precise and data-led. "casual": warm, plain English for a new fan, explains why it matters.
        - Headline at most 70 characters. Body at most 240 characters.
        Return JSON exactly: {"analyst":{"headline":"","body":""},"casual":{"headline":"","body":""}}
        """;

    private readonly IChatModel _chat;
    private readonly IContentSafety _safety;
    private readonly IAuditLog _audit;
    private readonly ILeagueReadModel _league;
    private readonly AiOptions _options;
    private readonly ILogger<NarrativeComposer> _logger;
    private readonly ConcurrentDictionary<string, int> _callsPerMatch = new(StringComparer.Ordinal);
    private readonly Lazy<IReadOnlyCollection<string>> _leagueNames;

    public NarrativeComposer(IChatModel chat, IContentSafety safety, IAuditLog audit, ILeagueReadModel league, IOptions<AiOptions> options, ILogger<NarrativeComposer> logger)
    {
        _chat = chat;
        _safety = safety;
        _audit = audit;
        _league = league;
        _options = options.Value;
        _logger = logger;
        _leagueNames = new Lazy<IReadOnlyCollection<string>>(() => _league.Clubs.SelectMany(c => c.Squad).Select(p => p.Name).ToList());
    }

    public IReadOnlyCollection<string> LeaguePlayerNames => _leagueNames.Value;

    public bool ShouldEnrich(Insight insight) =>
        _chat.IsConfigured
        && insight.Priority >= 4
        && insight.Kind is InsightKind.KeyMoment or InsightKind.Recap;

    public async Task<Insight> EnrichAsync(Insight draft, IReadOnlyCollection<string> matchPlayerNames, CancellationToken cancellationToken)
    {
        if (!ShouldEnrich(draft))
        {
            return draft;
        }

        var calls = _callsPerMatch.AddOrUpdate(draft.MatchId, 1, (_, n) => n + 1);
        if (calls > _options.MaxLlmCallsPerMatch)
        {
            _audit.Record("composer", "llm.budget", draft.MatchId, "skipped", $"Per-match budget of {_options.MaxLlmCallsPerMatch} calls reached");
            return draft;
        }

        var payload = JsonSerializer.Serialize(new
        {
            kind = draft.Kind.ToString(),
            minute = draft.Minute,
            facts = draft.Facts,
            names = matchPlayerNames.Where(n => Mentions(draft, n)).ToList(),
            template = new { analyst = draft.Analyst, casual = draft.Casual },
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));

        string? raw;
        try
        {
            raw = await _chat.CompleteJsonAsync(SystemPrompt, payload, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _audit.Record("composer", "llm.timeout", draft.MatchId, "fallback", draft.Id);
            return draft;
        }

        if (raw is null || !TryParse(raw, out var analyst, out var casual))
        {
            _audit.Record("composer", "llm.unusable", draft.MatchId, "fallback", draft.Id);
            return draft;
        }

        var violations = new List<string>();
        foreach (var variant in new[] { analyst, casual })
        {
            var result = OutputGuard.Check(variant, draft.Facts, matchPlayerNames, LeaguePlayerNames);
            violations.AddRange(result.Violations);
        }

        if (violations.Count == 0 && _safety.IsConfigured)
        {
            var safe = await _safety.IsSafeAsync($"{analyst.Headline} {analyst.Body} {casual.Headline} {casual.Body}", cancellationToken);
            if (!safe)
            {
                violations.Add("content safety");
            }
        }

        if (violations.Count > 0)
        {
            _audit.Record("guard", "llm.rejected", draft.MatchId, "fallback-to-template", $"{draft.Id}: {string.Join("; ", violations)}");
            _logger.LogInformation("Rejected generated text for {InsightId}: {Violations}", draft.Id, string.Join("; ", violations));
            return draft;
        }

        _audit.Record("guard", "llm.accepted", draft.MatchId, "published", draft.Id);
        return draft with { Analyst = analyst, Casual = casual, GeneratedBy = "llm" };
    }

    internal static bool TryParse(string raw, out TextVariant analyst, out TextVariant casual)
    {
        analyst = casual = new TextVariant(string.Empty, string.Empty);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            TextVariant Read(string name)
            {
                var node = doc.RootElement.GetProperty(name);
                return new TextVariant(
                    node.GetProperty("headline").GetString()?.Trim() ?? string.Empty,
                    node.GetProperty("body").GetString()?.Trim() ?? string.Empty);
            }

            analyst = Read("analyst");
            casual = Read("casual");
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool Mentions(Insight draft, string name) =>
        draft.Analyst.Body.Contains(name, StringComparison.Ordinal)
        || draft.Analyst.Headline.Contains(name, StringComparison.Ordinal)
        || draft.Casual.Body.Contains(name, StringComparison.Ordinal)
        || draft.Casual.Headline.Contains(name, StringComparison.Ordinal);
}
