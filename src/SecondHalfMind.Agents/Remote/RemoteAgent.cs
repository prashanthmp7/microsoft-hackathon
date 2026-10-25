using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents.Remote;

/// <summary>
/// Platform-side stand-in for an agent that runs as its own service. It forwards the event over the
/// transport and returns whatever comes back; the orchestrator then applies the charter of the agent
/// it represents, the time budget and every output guard, exactly as for an in-process agent.
/// </summary>
public sealed class RemoteAgent : IMatchAgent
{
    private readonly IRemoteAgentTransport _transport;
    private readonly ITrackingReadModel? _tracking;

    public RemoteAgent(string name, AgentStage stage, Type represents, IRemoteAgentTransport transport, TimeSpan timeout, ITrackingReadModel? tracking = null)
    {
        Name = name;
        Stage = stage;
        Represents = represents;
        Timeout = timeout;
        _transport = transport;
        _tracking = tracking;
    }

    public string Name { get; }

    public AgentStage Stage { get; }

    /// <summary>The agent class this stand-in speaks for; its charter is the one enforced.</summary>
    public Type Represents { get; }

    public TimeSpan Timeout { get; }

    public IEnumerable<Insight> OnEvent(AgentContext context)
    {
        var track = _tracking?.GetBallTrack(context.Match.Id, context.Event.Sequence);
        var request = AgentWire.ToRequest(context, Name, track);
        var response = _transport.Invoke(request, Timeout)
            ?? throw new TimeoutException($"{Name} service did not answer within {Timeout.TotalMilliseconds} ms");

        if (response.RequestId != request.RequestId || response.Agent != Name)
        {
            throw new InvalidOperationException($"{Name} service answered for another request or agent");
        }

        return response.Error is { } error
            ? throw new InvalidOperationException($"{Name} service failed: {error}")
            : response.Insights;
    }
}
