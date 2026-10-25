using System.Globalization;
using System.Text.RegularExpressions;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents.Guardrails;

public sealed record GuardResult(bool Passed, IReadOnlyList<string> Violations)
{
    public static GuardResult Pass { get; } = new(true, []);
}

/// <summary>
/// Deterministic checks every piece of generated text must pass before it can reach a screen.
/// <list type="bullet">
/// <item>Grounding: every number in the text must match a verified fact (as a value or a percentage).</item>
/// <item>Entities: no player may be named unless they are in this match.</item>
/// <item>Free-to-play: no betting or gambling language.</item>
/// <item>Render safety: no links, e-mail addresses, mentions, markup or template braces.</item>
/// <item>Size: headline and body limits so overlays always fit on screen.</item>
/// </list>
/// </summary>
public static partial class OutputGuard
{
    public const int MaxHeadlineLength = 90;
    public const int MaxBodyLength = 360;
    private const double Tolerance = 0.51;

    private static readonly string[] BannedTerms =
    [
        "bet", "bets", "betting", "odds", "wager", "gamble", "gambling", "bookmaker", "bookie", "accumulator",
        "parlay", "punt", "guaranteed", "sure thing", "lock of the day",
    ];

    public static GuardResult Check(
        TextVariant text,
        IReadOnlyDictionary<string, double> facts,
        IReadOnlyCollection<string> matchPlayerNames,
        IReadOnlyCollection<string> leaguePlayerNames)
    {
        var violations = new List<string>();
        if (string.IsNullOrWhiteSpace(text.Headline) || string.IsNullOrWhiteSpace(text.Body))
        {
            violations.Add("empty text");
            return new GuardResult(false, violations);
        }

        if (text.Headline.Length > MaxHeadlineLength)
        {
            violations.Add($"headline longer than {MaxHeadlineLength} characters");
        }

        if (text.Body.Length > MaxBodyLength)
        {
            violations.Add($"body longer than {MaxBodyLength} characters");
        }

        var combined = $"{text.Headline}\n{text.Body}";

        if (LinkPattern().IsMatch(combined))
        {
            violations.Add("contains a link, e-mail address or mention");
        }

        if (MarkupPattern().IsMatch(combined))
        {
            violations.Add("contains markup or template characters");
        }

        foreach (var term in BannedTerms)
        {
            if (Regex.IsMatch(combined, $@"\b{Regex.Escape(term)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            {
                violations.Add($"betting language: '{term}'");
            }
        }

        foreach (Match m in NumberPattern().Matches(combined))
        {
            var value = double.Parse(m.Value, CultureInfo.InvariantCulture);
            if (!IsGrounded(value, facts))
            {
                violations.Add($"ungrounded number: {m.Value}");
            }
        }

        var allowed = new HashSet<string>(matchPlayerNames, StringComparer.OrdinalIgnoreCase);
        foreach (var name in leaguePlayerNames)
        {
            if (!allowed.Contains(name) && combined.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"names a player not in this match: {name}");
            }
        }

        return violations.Count == 0 ? GuardResult.Pass : new GuardResult(false, violations);
    }

    public static bool IsGrounded(double value, IReadOnlyDictionary<string, double> facts)
    {
        // 0 and 1 are too common in prose ("one of", "1-0") to demand a fact.
        if (value is 0 or 1)
        {
            return true;
        }

        foreach (var fact in facts.Values)
        {
            var f = Math.Abs(fact);
            if (Math.Abs(value - f) <= Tolerance || Math.Abs(value - f * 100) <= Tolerance)
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"(https?://|www\.|\b\S+@\S+\.\S+\b|(^|\s)@\w+)", RegexOptions.IgnoreCase, 100)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"[<>{}\[\]\\`]", RegexOptions.None, 100)]
    private static partial Regex MarkupPattern();

    [GeneratedRegex(@"(?<![\w.])\d+(?:\.\d+)?", RegexOptions.None, 100)]
    private static partial Regex NumberPattern();
}
