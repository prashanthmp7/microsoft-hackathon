using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SecondHalfMind.Agents.Briefing;
using SecondHalfMind.Agents.Guardrails;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents.Orchestration;

/// <summary>
/// Supervisor for the agent team. For each ingested event it builds a read-only context and runs the
/// agents stage by stage (perception, analysis, interpretation, narrative, production); each stage
/// sees what earlier stages proposed. It then applies the supervisor policy (dedupe and on-screen pacing), runs the
/// guard, optionally hands top moments to the language model, and publishes to the insight sink.
/// A failing agent is isolated and audited; it never stops the match.
/// </summary>
public sealed class MatchOrchestrator
{
    private const int RecentWindowSeconds = 1500;

    private readonly IMatchReadModel _matches;
    private readonly ILeagueReadModel _league;
    private readonly IInsightReadModel _insightsRead;
    private readonly IFanPredictionReadModel _fans;
    private readonly IInsightSink _sink;
    private readonly IAuditLog _audit;
    private readonly BriefingService _briefings;
    private readonly NarrativeComposer _composer;
    private readonly IReadOnlyList<IMatchAgent> _agents;
    private readonly ILogger<MatchOrchestrator> _logger;
    private readonly ConcurrentDictionary<string, MatchRuntime> _runtimes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _llmConcurrency = new(2, 2);

    /// <summary>Time budget and quarantine for agents. Settable so tests can tighten the budget.</summary>
    public AgentGuard Guard { get; init; } = new();

    public MatchOrchestrator(
        IMatchReadModel matches,
        ILeagueReadModel league,
        IInsightReadModel insightsRead,
        IFanPredictionReadModel fans,
        IInsightSink sink,
        IAuditLog audit,
        BriefingService briefings,
        NarrativeComposer composer,
        IEnumerable<IMatchAgent> agents,
        ILogger<MatchOrchestrator> logger)
    {
        _matches = matches;
        _league = league;
        _insightsRead = insightsRead;
        _fans = fans;
        _sink = sink;
        _audit = audit;
        _briefings = briefings;
        _composer = composer;
        // Stable sort: stage order first, registration order within a stage.
        _agents = agents.OrderBy(a => a.Stage).ToList();
        _logger = logger;

        foreach (var agent in _agents.Where(a => !AgentCharter.IsChartered(a)))
        {
            // Not fatal (its output will be rejected), but loud: an agent with no charter does nothing.
            _logger.LogWarning("Agent {Agent} ({Type}) has no charter; all its output will be rejected", agent.Name, agent.GetType().Name);
            _audit.Record(agent.Name, "agent.uncharted", null, "blocked", agent.GetType().FullName ?? agent.GetType().Name);
        }
    }

    public IReadOnlyList<string> AgentNames => _agents.Select(a => a.Name).ToList();

    /// <summary>Per-agent counters for one match, in pipeline order. Read-only snapshot for the UI.</summary>
    public IReadOnlyList<AgentActivity> GetActivity(string matchId)
    {
        _runtimes.TryGetValue(matchId, out var runtime);
        return _agents.Select(a =>
        {
            var c = runtime?.CounterFor(a.Name);
            return new AgentActivity(a.Name, a.Stage.ToString(), c?.Proposed ?? 0, c?.Published ?? 0, c?.Held ?? 0,
                c?.Errors ?? 0, c?.LastHeadline, c?.LastClock, Guard.IsQuarantined(matchId, a.Name));
        }).ToList();
    }

    public Task HandleAsync(MatchEvent matchEvent, CancellationToken cancellationToken)
    {
        var match = _matches.GetMatch(matchEvent.MatchId);
        var snapshot = _matches.GetSnapshot(matchEvent.MatchId);
        var briefing = _briefings.Get(matchEvent.MatchId);
        var home = match is null ? null : _league.GetClub(match.HomeClubId);
        var away = match is null ? null : _league.GetClub(match.AwayClubId);
        if (match is null || snapshot is null || briefing is null || home is null || away is null)
        {
            return Task.CompletedTask;
        }

        var runtime = _runtimes.GetOrAdd(match.Id, _ => new MatchRuntime(home, away));
        runtime.Remember(matchEvent, RecentWindowSeconds);

        var context = new AgentContext(match, home, away, matchEvent, snapshot, runtime.Recent, briefing, _league,
            new MatchScopedInsights(_insightsRead, match.Id), new MatchScopedFans(_fans, match.Id));

        var proposed = new List<Insight>();
        foreach (var stage in _agents.GroupBy(a => a.Stage))
        {
            // Every agent in a stage sees the same upstream snapshot, so order within a stage doesn't matter.
            var stageContext = context with { Upstream = proposed.ToList().AsReadOnly() };
            var stageOutput = new List<Insight>();
            foreach (var agent in stage)
            {
                if (Guard.IsQuarantined(match.Id, agent.Name))
                {
                    continue;
                }

                try
                {
                    if (Guard.Run(agent, stageContext, AgentCharter.MaxInsightsPerEvent) is not { } result)
                    {
                        Reject(agent.Name, $"{matchEvent.Id}: exceeded {Guard.Budget.TotalMilliseconds} ms budget");
                        continue;
                    }

                    var output = result.ToList();
                    if (output.Count > AgentCharter.MaxInsightsPerEvent)
                    {
                        Reject(agent.Name, $"{matchEvent.Id}: more than {AgentCharter.MaxInsightsPerEvent} insights for one event");
                        output.RemoveRange(AgentCharter.MaxInsightsPerEvent, output.Count - AgentCharter.MaxInsightsPerEvent);
                    }

                    runtime.CounterFor(agent.Name).Proposed += output.Count;
                    foreach (var insight in output)
                    {
                        if (AgentCharter.Violation(agent, insight, matchEvent) is { } reason)
                        {
                            Reject(agent.Name, $"{insight.Id}: {reason}");
                            continue;
                        }

                        stageOutput.Add(insight);
                    }
                }
                catch (Exception ex)
                {
                    // Fault isolation: one agent failing must not take the others down.
                    var inner = ex is AggregateException { InnerException: { } i } ? i : ex;
                    runtime.CounterFor(agent.Name).Errors++;
                    _logger.LogError(inner, "Agent {Agent} failed on {EventId}", agent.Name, matchEvent.Id);
                    _audit.Record(agent.Name, "agent.error", match.Id, "isolated", $"{matchEvent.Id}: {inner.GetType().Name}");
                    StrikeOut(agent.Name);
                }
            }

            proposed.AddRange(stageOutput);
        }

        var accepted = SupervisorPolicy.Filter(proposed, runtime.Pacing).ToList();
        foreach (var held in proposed.Except(accepted))
        {
            runtime.CounterFor(held.Agent).Held++;
        }

        foreach (var insight in accepted)
        {
            var guard = OutputGuard.Check(insight.Analyst, insight.Facts, runtime.PlayerNames, _composer.LeaguePlayerNames);
            var guardCasual = OutputGuard.Check(insight.Casual, insight.Facts, runtime.PlayerNames, _composer.LeaguePlayerNames);
            if (!guard.Passed || !guardCasual.Passed)
            {
                // Strict: an insight whose text isn't grounded in its own facts is never shown.
                runtime.CounterFor(insight.Agent).Errors++;
                _audit.Record("guard", "template.violation", match.Id, "blocked",
                    $"{insight.Id}: {string.Join("; ", guard.Violations.Concat(guardCasual.Violations))}");
                continue;
            }

            if (_composer.ShouldEnrich(insight))
            {
                runtime.CounterFor(insight.Agent).Record(insight);
                _ = EnrichAndPublishAsync(insight, runtime.PlayerNames, cancellationToken);
                continue;
            }

            Publish(insight);
        }

        if (matchEvent.Type == MatchEventType.FullTime)
        {
            _audit.Record("orchestrator", "match.complete", match.Id, "ok", $"{runtime.Published} insights published");
        }

        return Task.CompletedTask;

        void Reject(string agentName, string detail)
        {
            runtime.CounterFor(agentName).Errors++;
            _audit.Record(agentName, "agent.rejected", match.Id, "blocked", detail);
            StrikeOut(agentName);
        }

        void StrikeOut(string agentName)
        {
            if (Guard.Strike(match.Id, agentName))
            {
                _logger.LogWarning("Agent {Agent} quarantined for match {MatchId}", agentName, match.Id);
                _audit.Record(agentName, "agent.quarantined", match.Id, "blocked",
                    $"{AgentGuard.StrikesBeforeQuarantine} strikes; no further output this match");
            }
        }

        void Publish(Insight insight)
        {
            _sink.Publish(insight);
            runtime.Published++;
            runtime.CounterFor(insight.Agent).Record(insight);
            if (insight.Priority >= 3)
            {
                _audit.Record(insight.Agent, "insight.publish", match.Id, insight.GeneratedBy, $"{insight.Kind} p{insight.Priority}: {insight.Analyst.Headline}");
            }
        }
    }

    private async Task EnrichAndPublishAsync(Insight draft, IReadOnlyCollection<string> playerNames, CancellationToken cancellationToken)
    {
        var insight = draft;
        await _llmConcurrency.WaitAsync(cancellationToken);
        try
        {
            insight = await _composer.EnrichAsync(draft, playerNames, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Enrichment failed for {InsightId}", draft.Id);
        }
        finally
        {
            _llmConcurrency.Release();
        }

        _sink.Publish(insight);
        _audit.Record(insight.Agent, "insight.publish", insight.MatchId, insight.GeneratedBy, $"{insight.Kind} p{insight.Priority}: {insight.Analyst.Headline}");
    }

    private sealed class MatchRuntime(Club home, Club away)
    {
        private readonly List<MatchEvent> _recent = [];

        public IReadOnlyCollection<string> PlayerNames { get; } = home.Squad.Concat(away.Squad).Select(p => p.Name).ToList();

        public SupervisorPolicy.PacingState Pacing { get; } = new();

        public int Published { get; set; }

        private readonly ConcurrentDictionary<string, AgentCounter> _counters = new(StringComparer.Ordinal);

        public AgentCounter CounterFor(string agent) => _counters.GetOrAdd(agent, _ => new AgentCounter());

        public IReadOnlyList<MatchEvent> Recent => _recent.ToList();

        public void Remember(MatchEvent e, int windowSeconds)
        {
            _recent.Add(e);
            var cutoff = e.ClockSeconds - windowSeconds;
            var drop = 0;
            while (drop < _recent.Count && _recent[drop].ClockSeconds < cutoff)
            {
                drop++;
            }

            if (drop > 0)
            {
                _recent.RemoveRange(0, drop);
            }
        }
    }
}

/// <summary>What one agent has done in one match so far, for the agent team panel.</summary>
public sealed record AgentActivity(
    string Agent, string Stage, int Proposed, int Published, int Held, int Errors, string? LastHeadline, int? LastClock, bool Quarantined = false);

/// <summary>Mutable per-match counters, owned by the orchestrator only (events are handled sequentially per match).</summary>
internal sealed class AgentCounter
{
    public int Proposed { get; set; }
    public int Published { get; set; }
    public int Held { get; set; }
    public int Errors { get; set; }
    public string? LastHeadline { get; private set; }
    public int? LastClock { get; private set; }

    public void Record(Insight insight)
    {
        Published++;
        LastHeadline = insight.Analyst.Headline;
        LastClock = insight.ClockSeconds;
    }
}

/// <summary>
/// Pacing rules so screens are never flooded
/// per-player action tags are spaced per player, and anything at priority 3+ always goes through.
/// </summary>
public static class SupervisorPolicy
{
    public const int MetricGapSeconds = 90;
    public const int PlayerGapSeconds = 45;

    public sealed class PacingState
    {
        public Dictionary<string, int> LastByMetric { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> LastByPlayer { get; } = new(StringComparer.Ordinal);
    }

    public static IEnumerable<Insight> Filter(IEnumerable<Insight> proposed, PacingState state)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var insight in proposed.OrderByDescending(i => i.Priority))
        {
            if (!seen.Add(insight.Id))
            {
                continue;
            }

            if (insight.Priority >= 3 || insight.Kind == InsightKind.WinProbability)
            {
                yield return insight;
                continue;
            }

            if (insight.Metric == "playerAction" && insight.PlayerId is not null)
            {
                if (state.LastByPlayer.TryGetValue(insight.PlayerId, out var lastPlayer) && insight.ClockSeconds - lastPlayer < PlayerGapSeconds)
                {
                    continue;
                }

                state.LastByPlayer[insight.PlayerId] = insight.ClockSeconds;
                yield return insight;
                continue;
            }

            var key = insight.Kind == InsightKind.PlayerAlert || insight.Kind == InsightKind.Milestone
                ? $"{insight.Metric}:{insight.PlayerId}"
                : insight.Metric ?? insight.Kind.ToString();
            if (state.LastByMetric.TryGetValue(key, out var last) && insight.ClockSeconds - last < MetricGapSeconds)
            {
                continue;
            }

            state.LastByMetric[key] = insight.ClockSeconds;
            yield return insight;
        }
    }
}
