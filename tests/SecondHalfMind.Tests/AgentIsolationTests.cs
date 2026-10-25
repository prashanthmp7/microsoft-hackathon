using SecondHalfMind.Agents;
using SecondHalfMind.Agents.Orchestration;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Tests;

/// <summary>Agents can't speak for, overwrite or tamper with one another, and stay within their roles.</summary>
public class AgentIsolationTests
{
    private sealed class FakeAgent(string name) : IMatchAgent
    {
        public string Name => name;
        public AgentStage Stage => AgentStage.Analysis;
        public IEnumerable<Insight> OnEvent(AgentContext context) => [];
    }

    private static readonly MatchEvent Source = new()
    {
        Id = "m1-e42", MatchId = "m1", Sequence = 42, ClockSeconds = 600, Type = MatchEventType.Shot, Team = Side.Home,
    };

    private static Insight Make(string agent, InsightKind kind = InsightKind.Stat, int priority = 2, string? id = null, string generatedBy = "template") => new()
    {
        Id = id ?? $"{Source.Id}-{agent}-{kind}".ToLowerInvariant(),
        MatchId = Source.MatchId,
        SourceSequence = Source.Sequence,
        ClockSeconds = Source.ClockSeconds,
        Minute = Source.Minute,
        Kind = kind,
        Agent = agent,
        Priority = priority,
        Analyst = new TextVariant("h", "b"),
        Casual = new TextVariant("h", "b"),
        GeneratedBy = generatedBy,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static readonly Dictionary<string, Type> Types = new()
    {
        ["vision"] = typeof(SecondHalfMind.Agents.Vision.AutoEventingAgent),
        ["stats"] = typeof(StatsAgent),
        ["explainer"] = typeof(ExplainerAgent),
        ["referee"] = typeof(PredictionRefereeAgent),
        ["narrator"] = typeof(NarratorAgent),
        ["producer"] = typeof(ProducerAgent),
    };

    private static string? Check(string author, Insight insight) =>
        AgentCharter.Violation(Types[author], author, insight, Source);

    [Fact]
    public void Insight_within_the_charter_is_allowed() =>
        Assert.Null(Check("stats", Make("stats")));

    [Fact]
    public void Agent_cannot_impersonate_another_agent() =>
        Assert.NotNull(Check("stats", Make("narrator", InsightKind.Recap)));

    [Fact]
    public void Agent_cannot_borrow_another_agents_name() =>
        Assert.NotNull(AgentCharter.Violation(typeof(StatsAgent), "narrator", Make("narrator", InsightKind.Recap), Source));

    [Fact]
    public void Agent_cannot_reuse_another_agents_insight_id() =>
        Assert.NotNull(Check("stats", Make("stats", id: $"{Source.Id}-explainer-keymoment")));

    [Fact]
    public void Agent_cannot_publish_outside_its_role_or_priority()
    {
        Assert.NotNull(Check("vision", Make("vision", InsightKind.KeyMoment)));
        Assert.NotNull(Check("vision", Make("vision", priority: 5)));
        Assert.NotNull(Check("producer", Make("producer", InsightKind.PredictionResolved)));
    }

    [Fact]
    public void Agent_cannot_claim_generated_text_or_target_another_event()
    {
        Assert.NotNull(Check("stats", Make("stats", generatedBy: "llm")));
        Assert.NotNull(Check("stats", Make("stats") with { SourceSequence = 7 }));
    }

    [Fact]
    public void Unknown_agent_types_are_allowed_nothing()
    {
        Assert.NotNull(AgentCharter.Violation(new FakeAgent("rogue"), Make("rogue"), Source));
        // A fake agent that copies a real agent's name still has no charter.
        Assert.NotNull(AgentCharter.Violation(new FakeAgent("stats"), Make("stats"), Source));
    }

    [Fact]
    public void Agent_that_overruns_its_budget_is_cut_off_and_quarantined()
    {
        var guard = new AgentGuard { Budget = TimeSpan.FromMilliseconds(20) };
        Assert.Null(guard.Run(new SlowAgent(), null!, AgentCharter.MaxInsightsPerEvent));
        for (var i = 1; i < AgentGuard.StrikesBeforeQuarantine; i++)
        {
            Assert.False(guard.Strike("m1", "slow"));
        }

        Assert.True(guard.Strike("m1", "slow"));
        Assert.True(guard.IsQuarantined("m1", "slow"));
        Assert.False(guard.IsQuarantined("m2", "slow"));
    }

    private sealed class SlowAgent : IMatchAgent
    {
        public string Name => "slow";
        public AgentStage Stage => AgentStage.Analysis;
        public IEnumerable<Insight> OnEvent(AgentContext context)
        {
            Thread.Sleep(500);
            return [];
        }
    }

    [Fact]
    public void Shared_insight_facts_and_tags_cannot_be_changed_by_a_later_agent()
    {
        var facts = new Dictionary<string, double> { ["speed"] = 100 };
        var insight = Make("stats") with { Facts = facts, Tags = new Dictionary<string, string> { ["k"] = "v" } };

        // Changing the caller's dictionary afterwards must not leak into the published insight.
        facts["speed"] = 1;
        Assert.Equal(100, insight.Facts["speed"]);

        // Casting back to a mutable type must fail.
        Assert.IsNotType<Dictionary<string, double>>(insight.Facts);
        if (insight.Facts is IDictionary<string, double> asMutable)
        {
            Assert.Throws<NotSupportedException>(() => asMutable["speed"] = 1);
        }

        if (insight.Tags is IDictionary<string, string> tags)
        {
            Assert.Throws<NotSupportedException>(() => tags["k"] = "x");
        }
    }

    [Fact]
    public void Upstream_handoff_is_read_only()
    {
        IReadOnlyList<Insight> upstream = new List<Insight> { Make("stats") }.AsReadOnly();
        Assert.Throws<NotSupportedException>(() => ((IList<Insight>)upstream).Add(Make("stats")));
    }

    [Fact]
    public void Every_registered_agent_has_a_charter()
    {
        foreach (var (name, type) in Types)
        {
            Assert.True(AgentCharter.IsChartered(type, name), name);
        }
    }
}
