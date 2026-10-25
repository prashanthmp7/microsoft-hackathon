using SecondHalfMind.Agents.Guardrails;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Tests;

public class OutputGuardTests
{
    private static readonly Dictionary<string, double> Facts = new()
    {
        ["wpBefore"] = 0.32,
        ["wpAfter"] = 0.61,
        ["xg"] = 0.11,
        ["minute"] = 31,
        ["homeGoals"] = 1,
        ["awayGoals"] = 0,
    };

    private static readonly string[] MatchPlayers = ["Femi Tordale", "Ivo Iverwood"];
    private static readonly string[] LeaguePlayers = ["Femi Tordale", "Ivo Iverwood", "Leo Ashby"];

    private static GuardResult Check(string headline, string body) =>
        OutputGuard.Check(new TextVariant(headline, body), Facts, MatchPlayers, LeaguePlayers);

    [Fact]
    public void Accepts_text_grounded_in_facts()
    {
        var result = Check("GOAL! Femi Tordale scores (31')", "1-0. A chance like that goes in 11% of the time; win chance 32% to 61%. xG 0.11.");

        Assert.True(result.Passed, string.Join("; ", result.Violations));
    }

    [Fact]
    public void Rejects_numbers_that_are_not_in_the_facts()
    {
        var result = Check("GOAL!", "Win chance is now 78%.");

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Contains("78", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Great odds on a home win now.")]
    [InlineData("Time to bet on Northvale.")]
    [InlineData("A guaranteed winner.")]
    public void Rejects_betting_language(string body)
    {
        Assert.False(Check("Swing", body).Passed);
    }

    [Theory]
    [InlineData("Read more at https://example.com")]
    [InlineData("Follow @someone for more")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("Template {name} leak")]
    public void Rejects_links_mentions_and_markup(string body)
    {
        Assert.False(Check("Swing", body).Passed);
    }

    [Fact]
    public void Rejects_players_who_are_not_in_this_match()
    {
        var result = Check("Leo Ashby scores", "What a finish.");

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Contains("Leo Ashby", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_overlong_text()
    {
        Assert.False(Check(new string('a', OutputGuard.MaxHeadlineLength + 1), "Body.").Passed);
        Assert.False(Check("Headline", new string('a', OutputGuard.MaxBodyLength + 1)).Passed);
    }

    [Fact]
    public void Composer_parses_only_the_expected_shape()
    {
        Assert.True(NarrativeComposer.TryParse("""{"analyst":{"headline":"A","body":"B"},"casual":{"headline":"C","body":"D"}}""", out var a, out var c));
        Assert.Equal("A", a.Headline);
        Assert.Equal("D", c.Body);
        Assert.False(NarrativeComposer.TryParse("""{"analyst":"oops"}""", out _, out _));
        Assert.False(NarrativeComposer.TryParse("not json", out _, out _));
    }
}
