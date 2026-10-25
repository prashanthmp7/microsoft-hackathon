using SecondHalfMind.Agents.Orchestration;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Api.Services;

/// <summary>Runs the agent orchestrator over the read-only event feed.</summary>
public sealed class AgentHost : BackgroundService
{
    private readonly IMatchEventFeed _feed;
    private readonly MatchOrchestrator _orchestrator;
    private readonly ILogger<AgentHost> _logger;

    public AgentHost(IMatchEventFeed feed, MatchOrchestrator orchestrator, ILogger<AgentHost> logger)
    {
        _feed = feed;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Agent team online: {Agents}", string.Join(", ", _orchestrator.AgentNames));
        await foreach (var matchEvent in _feed.ReadAllAsync(stoppingToken))
        {
            try
            {
                await _orchestrator.HandleAsync(matchEvent, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Orchestrator failed on {EventId}", matchEvent.Id);
            }
        }
    }
}
