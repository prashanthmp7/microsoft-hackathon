using System.Text.Json;
using SecondHalfMind.Agents;
using SecondHalfMind.Agents.Hosting;
using SecondHalfMind.Agents.Remote;
using SecondHalfMind.Agents.Vision;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Tests;

/// <summary>
/// Agents hosted as separate services. A loopback transport serialises every request and response
/// through JSON exactly as Service Bus would, and each "service" owns its own agent instance with
/// no access to platform stores.
/// </summary>
public class RemoteAgentTests
{
    private sealed class LoopbackTransport : IRemoteAgentTransport
    {
        private readonly Dictionary<string, IMatchAgent> _services;

        public LoopbackTransport()
        {
            var tracking = new RequestTracking();
            _services = new IMatchAgent[]
            {
                new AutoEventingAgent(tracking), new StatsAgent(), new ExplainerAgent(),
                new PredictionRefereeAgent(), new NarratorAgent(), new ProducerAgent(),
            }.ToDictionary(a => a.Name);
        }

        public Func<AgentResponse, AgentResponse> Tamper { get; set; } = r => r;

        public AgentResponse? Invoke(AgentRequest request, TimeSpan timeout)
        {
            var wire = JsonSerializer.Deserialize<AgentRequest>(JsonSerializer.Serialize(request, AgentWire.Json), AgentWire.Json)!;
            var response = AgentServiceWorker.Handle(_services[wire.Agent], wire);
            return Tamper(JsonSerializer.Deserialize<AgentResponse>(JsonSerializer.Serialize(response, AgentWire.Json), AgentWire.Json)!);
        }
    }

    private static PipelineTests.Harness RemoteHarness(LoopbackTransport transport) =>
        new(tracking => AgentServiceHost.Catalog.Select(c =>
            (IMatchAgent)new RemoteAgent(c.Name, c.Stage, c.Type, transport, TimeSpan.FromSeconds(2), c.Type == typeof(AutoEventingAgent) ? tracking : null)));

    [Fact]
    public void Remote_agents_produce_the_same_insights_as_in_process_agents()
    {
        var local = new PipelineTests.Harness();
        var localMatch = local.Play("northvale", "redcastle", 7);

        var remote = RemoteHarness(new LoopbackTransport());
        var remoteMatch = remote.Play("northvale", "redcastle", 7);

        static IEnumerable<string> Shape(IEnumerable<Insight> insights, string matchId) =>
            insights.Select(i => $"{i.Id.Replace(matchId, "M", StringComparison.Ordinal)}|{i.Kind}|{i.Priority}|{i.Analyst.Headline}");

        Assert.Equal(
            Shape(local.Insights.GetInsights(localMatch.Id, 0, 2000), localMatch.Id),
            Shape(remote.Insights.GetInsights(remoteMatch.Id, 0, 2000), remoteMatch.Id));
        Assert.Empty(remote.Audit.Recent(remoteMatch.Id, 2000).Where(a => a.Action is "agent.error" or "agent.rejected"));
    }

    [Fact]
    public void Remote_service_is_still_held_to_its_charter()
    {
        var transport = new LoopbackTransport
        {
            // A compromised stats service tries to publish as the narrator.
            Tamper = r => r.Agent == "stats"
                ? r with { Insights = r.Insights.Select(i => i with { Agent = "narrator", Kind = InsightKind.Recap, Priority = 5 }).ToList() }
                : r,
        };
        var h = RemoteHarness(transport);
        var match = h.Play("northvale", "redcastle", 7);

        Assert.DoesNotContain(h.Insights.GetInsights(match.Id, 0, 2000), i => i.Id.Contains("-stats-", StringComparison.Ordinal));
        Assert.Contains(h.Audit.Recent(match.Id, 2000), a => a.Action == "agent.quarantined" && a.Actor == "stats");
    }

    [Fact]
    public void Answer_for_another_request_is_rejected()
    {
        var transport = new LoopbackTransport { Tamper = r => r with { RequestId = "forged" } };
        var h = RemoteHarness(transport);
        var match = h.Play("northvale", "redcastle", 7);

        Assert.Empty(h.Insights.GetInsights(match.Id, 0, 2000).Where(i => i.Agent is "vision" or "stats" or "narrator" or "producer"));
    }

    [Fact]
    public void Agent_service_refuses_requests_for_other_agents()
    {
        var response = AgentServiceWorker.Handle(new StatsAgent(), new AgentRequest(
            "r1", "narrator", null!, null!, null!, null!, null!, [], null!, null!, [], [], null!, null));
        Assert.NotNull(response.Error);
        Assert.Empty(response.Insights);
    }
}
