using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SecondHalfMind.Agents;
using SecondHalfMind.Agents.Ai;
using SecondHalfMind.Agents.Briefing;
using SecondHalfMind.Agents.Guardrails;
using SecondHalfMind.Agents.Models;
using SecondHalfMind.Agents.Orchestration;
using SecondHalfMind.Agents.Vision;
using SecondHalfMind.Data;
using SecondHalfMind.Domain;
using SecondHalfMind.Simulation;

namespace SecondHalfMind.Tests;

/// <summary>Runs whole synthetic matches through ingest, the agent team and the ledger, without timing.</summary>
public class PipelineTests
{
    internal sealed class Harness
    {
        public Harness(Func<ITrackingReadModel, IEnumerable<IMatchAgent>>? agents = null)
        {
            League = new LeagueStore(CalibrationProfile.Defaults);
            Store = new InMemoryMatchStore(League);
            Insights = new InsightStore();
            Ledger = new PredictionLedger(Store, Insights);
            Audit = new InMemoryAuditLog();
            var model = new PreMatchModel(League);
            Briefings = new BriefingService(League, Store, model);
            var options = Options.Create(new AiOptions());
            var tokens = new ManagedIdentityTokenProvider(new HttpClient(), options);
            var composer = new NarrativeComposer(
                new FoundryChatModel(new HttpClient(), options, tokens, NullLogger<FoundryChatModel>.Instance),
                new AzureContentSafety(new HttpClient(), options, tokens),
                Audit, League, options, NullLogger<NarrativeComposer>.Instance);
            Tracking = new InMemoryTrackingStore();
            Orchestrator = new MatchOrchestrator(Store, League, Insights, Ledger, Insights, Audit, Briefings, composer,
                agents?.Invoke(Tracking) ?? [new ProducerAgent(), new StatsAgent(), new AutoEventingAgent(Tracking), new ExplainerAgent(), new PredictionRefereeAgent(), new NarratorAgent()],
                NullLogger<MatchOrchestrator>.Instance);
        }

        public LeagueStore League { get; }
        public InMemoryMatchStore Store { get; }
        public InsightStore Insights { get; }
        public PredictionLedger Ledger { get; }
        public InMemoryAuditLog Audit { get; }
        public InMemoryTrackingStore Tracking { get; }
        public BriefingService Briefings { get; }
        public MatchOrchestrator Orchestrator { get; }

        public MatchInfo Play(string home, string away, int seed, Action<MatchInfo>? beforeKickOff = null)
        {
            var match = Store.Register(home, away, seed);
            beforeKickOff?.Invoke(match);
            Store.MarkLive(match.Id);
            foreach (var e in MatchSimulator.Simulate(match, League.GetClub(home)!, League.GetClub(away)!, League.Calibration))
            {
                if (TrackingSynthesizer.ForEvent(e) is { } track)
                {
                    Tracking.Append(track);
                }

                Store.Append(e);
                Orchestrator.HandleAsync(e, CancellationToken.None).GetAwaiter().GetResult();
            }

            Store.MarkFinished(match.Id);
            return match;
        }
    }

    [Theory]
    [InlineData("northvale", "redcastle", 101)]
    [InlineData("ashbury", "ravenport", 202)]
    [InlineData("kingsmere", "harbourside", 303)]
    public void Every_template_insight_passes_the_output_guard(string home, string away, int seed)
    {
        var h = new Harness();
        var match = h.Play(home, away, seed);

        var violations = h.Audit.Recent(match.Id, 1000).Where(a => a.Action.Contains("violation", StringComparison.Ordinal)).ToList();
        var errors = h.Audit.Recent(match.Id, 1000).Where(a => a.Action is "agent.error" or "agent.rejected").ToList();

        Assert.Empty(violations);
        Assert.Empty(errors);
    }

    [Fact]
    public void A_full_match_produces_every_stage_of_output()
    {
        var h = new Harness();
        var match = h.Play("northvale", "redcastle", 7);
        var insights = h.Insights.GetInsights(match.Id, 0, 1000);
        var kinds = insights.Select(i => i.Kind).ToHashSet();

        Assert.Contains(InsightKind.Stat, kinds);
        Assert.Contains(InsightKind.WinProbability, kinds);
        Assert.Contains(InsightKind.PredictionOpened, kinds);
        Assert.Contains(InsightKind.PredictionResolved, kinds);
        Assert.Contains(InsightKind.Recap, kinds);
        Assert.Contains(insights, i => i.Tags.GetValueOrDefault("question") == "prematch");
        Assert.Contains(insights, i => i.Agent == "vision" && i.Metric == "autoEventing");
        Assert.All(insights, i => Assert.Equal("template", i.GeneratedBy));
    }

    [Fact]
    public void Agents_run_in_stage_order_whatever_the_registration_order()
    {
        var h = new Harness();
        Assert.Equal(["vision", "stats", "explainer", "referee", "narrator", "producer"], h.Orchestrator.AgentNames);
    }

    [Fact]
    public void Producer_cuts_the_reel_from_upstream_and_published_moments()
    {
        var h = new Harness();
        var match = h.Play("northvale", "redcastle", 7);
        var insights = h.Insights.GetInsights(match.Id, 0, 1000);
        var cuts = insights.Where(i => i.Agent == "producer" && i.Metric == ProducerAgent.Metric).ToList();

        Assert.Equal(2, cuts.Count);
        var ids = insights.Select(i => i.Id).ToHashSet();
        foreach (var cut in cuts)
        {
            var picks = cut.Tags[ProducerAgent.PicksTag].Split(',');
            Assert.Equal(cut.Facts["clips"], picks.Length);
            Assert.All(picks, id => Assert.Contains(id, ids));
        }
    }

    [Fact]
    public void Narrator_builds_on_the_vision_agent_and_activity_is_reported()
    {
        var h = new Harness();
        var match = h.Play("northvale", "redcastle", 7);
        var recap = h.Insights.GetInsights(match.Id, 0, 1000).Single(i => i.Agent == "narrator" && i.Id.EndsWith("-ft", StringComparison.Ordinal));

        Assert.True(recap.Facts.ContainsKey("trackedShotKmh"));
        Assert.StartsWith(match.Id, recap.Tags["buildsOn"], StringComparison.Ordinal);

        var activity = h.Orchestrator.GetActivity(match.Id);
        Assert.Equal(h.Orchestrator.AgentNames, activity.Select(a => a.Agent));
        Assert.All(activity, a => Assert.True(a.Published > 0, a.Agent));
        Assert.All(activity, a => Assert.Equal(0, a.Errors));
        Assert.Contains(activity, a => a.Held > 0);
    }

    [Fact]
    public void Agents_never_change_the_event_log()
    {
        var h = new Harness();
        var match = h.Store.Register("westbrook", "elmfield", 11);
        var events = MatchSimulator.Simulate(match, h.League.GetClub("westbrook")!, h.League.GetClub("elmfield")!, h.League.Calibration);
        h.Store.MarkLive(match.Id);
        foreach (var e in events)
        {
            h.Store.Append(e);
            h.Orchestrator.HandleAsync(e, CancellationToken.None).GetAwaiter().GetResult();
        }

        var stored = h.Store.GetEvents(match.Id);
        Assert.Equal(events.Count, stored.Count);
        Assert.True(events.SequenceEqual(stored), "Stored events must be exactly the ingested events.");
    }

    [Fact]
    public void Event_store_rejects_replayed_or_reordered_events()
    {
        var h = new Harness();
        var match = h.Store.Register("oakridge", "larkspur", 3);
        var events = MatchSimulator.Simulate(match, h.League.GetClub("oakridge")!, h.League.GetClub("larkspur")!, h.League.Calibration);
        h.Store.Append(events[0]);
        h.Store.Append(events[1]);

        Assert.Throws<InvalidOperationException>(() => h.Store.Append(events[1]));
        Assert.Throws<InvalidOperationException>(() => h.Store.Append(events[0]));
    }

    [Fact]
    public void Ledger_scores_fans_and_closes_picks_at_kick_off()
    {
        var h = new Harness();
        var fanId = Guid.NewGuid().ToString();
        FanPick? pick = null;
        var match = h.Play("northvale", "redcastle", 7, m =>
        {
            pick = h.Ledger.SubmitPick(new FanPick(fanId, "Tester", m.Id, new ScorePrediction(MatchOutcome.Home, 2, 1), DateTimeOffset.UtcNow));
        });

        Assert.NotNull(pick);
        Assert.Throws<InvalidOperationException>(() =>
            h.Ledger.SubmitPick(new FanPick(Guid.NewGuid().ToString(), "Late", match.Id, new ScorePrediction(MatchOutcome.Draw, 0, 0), DateTimeOffset.UtcNow)));

        var board = h.Ledger.GetLeaderboard(match.Id);
        var fan = Assert.Single(board.Fans);
        Assert.Equal(1, fan.Answered);
        Assert.True(board.Model.Answered >= 2, "Model should be scored on the pre-match call and micro questions.");
    }
}
