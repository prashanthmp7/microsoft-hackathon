using System.Collections.Concurrent;
using SecondHalfMind.Agents.Vision;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents.Orchestration;

/// <summary>
/// What each agent is allowed to publish. Charters are bound to the agent's concrete type, not the
/// name it reports, so an agent cannot rename itself to borrow another's permissions. The orchestrator
/// checks every proposed insight against its author's charter before pacing, so one agent can never
/// speak as another, overwrite another's insight id, publish outside its role, or flood the team.
/// Unknown agent types are allowed nothing.
/// </summary>
public static class AgentCharter
{
    public const int MaxInsightsPerEvent = 10;

    private sealed record Charter(string Name, HashSet<InsightKind> Kinds, int MaxPriority);

    private static readonly Dictionary<Type, Charter> Charters = new()
    {
        [typeof(AutoEventingAgent)] = new("vision", [InsightKind.Stat], 2),
        [typeof(StatsAgent)] = new("stats", [InsightKind.Stat, InsightKind.Milestone, InsightKind.PlayerAlert], 4),
        [typeof(ExplainerAgent)] = new("explainer", [InsightKind.WinProbability, InsightKind.KeyMoment, InsightKind.Stat], 5),
        [typeof(PredictionRefereeAgent)] = new("referee", [InsightKind.PredictionOpened, InsightKind.PredictionResolved], 5),
        [typeof(NarratorAgent)] = new("narrator", [InsightKind.Recap], 5),
        [typeof(ProducerAgent)] = new("producer", [InsightKind.Recap], 3),
    };

    /// <summary>True when the agent's type has a charter and it reports the name the charter expects.</summary>
    public static bool IsChartered(IMatchAgent agent) => IsChartered(CharterType(agent), agent.Name);

    /// <summary>A remote stand-in is held to the charter of the agent class it represents.</summary>
    private static Type CharterType(IMatchAgent agent) =>
        agent is Remote.RemoteAgent remote ? remote.Represents : agent.GetType();

    /// <summary>Type-based form, used where constructing the agent is unnecessary (tests, startup checks).</summary>
    public static bool IsChartered(Type agentType, string name) =>
        Charters.TryGetValue(agentType, out var c) && c.Name == name;

    /// <summary>Returns null when the insight is allowed, otherwise the reason it was rejected.</summary>
    public static string? Violation(IMatchAgent author, Insight insight, MatchEvent source) =>
        Violation(CharterType(author), author.Name, insight, source);

    /// <inheritdoc cref="Violation(IMatchAgent, Insight, MatchEvent)"/>
    public static string? Violation(Type authorType, string authorName, Insight insight, MatchEvent source)
    {
        if (!Charters.TryGetValue(authorType, out var charter))
        {
            return $"type {authorType.Name} has no charter";
        }

        if (!string.Equals(authorName, charter.Name, StringComparison.Ordinal))
        {
            return $"type is chartered as '{charter.Name}' but reports '{authorName}'";
        }

        if (!string.Equals(insight.Agent, charter.Name, StringComparison.Ordinal))
        {
            return $"claims to be '{insight.Agent}'";
        }

        if (!insight.Id.StartsWith($"{source.Id}-{charter.Name}-", StringComparison.OrdinalIgnoreCase))
        {
            return "id outside the agent's namespace";
        }

        if (insight.MatchId != source.MatchId || insight.SourceSequence != source.Sequence)
        {
            return "not about the current event";
        }

        if (!charter.Kinds.Contains(insight.Kind))
        {
            return $"kind {insight.Kind} is outside its role";
        }

        if (insight.Priority > charter.MaxPriority)
        {
            return $"priority {insight.Priority} above its cap {charter.MaxPriority}";
        }

        if (insight.GeneratedBy != "template")
        {
            return "only the narrative composer may mark text as generated";
        }

        return null;
    }
}

/// <summary>
/// Runtime containment for agents that share one process: a time budget per call and a per-match
/// circuit breaker. An agent that times out, throws or breaks its charter too often is quarantined
/// for the rest of that match, so a misbehaving agent degrades its own output and nobody else's.
/// </summary>
public sealed class AgentGuard
{
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromMilliseconds(250);
    public const int StrikesBeforeQuarantine = 5;

    private readonly ConcurrentDictionary<(string Match, string Agent), int> _strikes = new();

    public TimeSpan Budget { get; init; } = DefaultBudget;

    public bool IsQuarantined(string matchId, string agent) =>
        _strikes.TryGetValue((matchId, agent), out var s) && s >= StrikesBeforeQuarantine;

    /// <summary>Records a strike; returns true when this strike quarantines the agent.</summary>
    public bool Strike(string matchId, string agent) =>
        _strikes.AddOrUpdate((matchId, agent), 1, (_, s) => s + 1) == StrikesBeforeQuarantine;

    /// <summary>
    /// Runs the agent with a time budget. The call runs on the thread pool; if it overruns, its
    /// output is discarded (agents are side-effect free, so abandoning the result is safe).
    /// </summary>
    public IReadOnlyList<Insight>? Run(IMatchAgent agent, AgentContext context, int maxInsights)
    {
        // Remote agents add a queue round trip, so they get their own (configured) budget.
        var budget = agent is Remote.RemoteAgent remote ? remote.Timeout + TimeSpan.FromMilliseconds(100) : Budget;
        var task = Task.Run(() => agent.OnEvent(context).Take(maxInsights + 1).ToList());
        if (!task.Wait(budget))
        {
            // Observe a late fault so it never surfaces as an unobserved task exception.
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return null;
        }

        return task.Result;
    }
}
