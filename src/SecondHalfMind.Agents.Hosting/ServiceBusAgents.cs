using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecondHalfMind.Agents.Remote;

namespace SecondHalfMind.Agents.Hosting;

public sealed class AgentBusOptions
{
    public const string SectionName = "Agents";

    /// <summary>"InProcess" (default) or "ServiceBus".</summary>
    public string Mode { get; set; } = "InProcess";

    /// <summary>Fully qualified namespace, e.g. shm-bus.servicebus.windows.net. No connection strings.</summary>
    public string? ServiceBusNamespace { get; set; }

    /// <summary>How long the platform waits for a remote agent per event.</summary>
    public int TimeoutMs { get; set; } = 2000;

    public bool UseServiceBus => string.Equals(Mode, "ServiceBus", StringComparison.OrdinalIgnoreCase);

    internal ServiceBusClient CreateClient() =>
        new(ServiceBusNamespace ?? throw new InvalidOperationException("Agents:ServiceBusNamespace is required in ServiceBus mode."),
            new DefaultAzureCredential());
}

/// <summary>
/// Platform side: sends each request to the agent's own queue and matches answers from the shared
/// results queue by request id. Identity is managed identity; RBAC lets the platform send to agent
/// queues and receive results, nothing more.
/// </summary>
public sealed class ServiceBusAgentTransport : IRemoteAgentTransport, IHostedService, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AgentResponse>> _pending = new(StringComparer.Ordinal);
    private readonly ServiceBusProcessor _results;
    private readonly ILogger<ServiceBusAgentTransport> _logger;

    public ServiceBusAgentTransport(IOptions<AgentBusOptions> options, ILogger<ServiceBusAgentTransport> logger)
    {
        _client = options.Value.CreateClient();
        _logger = logger;
        _results = _client.CreateProcessor(AgentWire.ResultsQueue, new ServiceBusProcessorOptions { MaxConcurrentCalls = 8 });
        _results.ProcessMessageAsync += OnResult;
        _results.ProcessErrorAsync += e =>
        {
            _logger.LogWarning(e.Exception, "Agent results receiver error");
            return Task.CompletedTask;
        };
    }

    public AgentResponse? Invoke(AgentRequest request, TimeSpan timeout)
    {
        var waiter = new TaskCompletionSource<AgentResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.RequestId] = waiter;
        try
        {
            var sender = _senders.GetOrAdd(request.Agent, a => _client.CreateSender(AgentWire.QueueFor(a)));
            var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(request, AgentWire.Json))
            {
                MessageId = request.RequestId,
                ContentType = "application/json",
                TimeToLive = timeout, // a late request is useless; let the broker drop it
            };
            sender.SendMessageAsync(message).Wait(timeout);
            return waiter.Task.Wait(timeout) ? waiter.Task.Result : null;
        }
        finally
        {
            _pending.TryRemove(request.RequestId, out _);
        }
    }

    private async Task OnResult(ProcessMessageEventArgs args)
    {
        AgentResponse? response = null;
        try
        {
            response = args.Message.Body.ToObjectFromJson<AgentResponse>(AgentWire.Json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Discarded malformed agent response");
        }

        if (response is not null && _pending.TryGetValue(response.RequestId, out var waiter))
        {
            waiter.TrySetResult(response);
        }

        // Late or unknown answers are simply dropped: the orchestrator has already moved on.
        await args.CompleteMessageAsync(args.Message);
    }

    public Task StartAsync(CancellationToken cancellationToken) => _results.StartProcessingAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => _results.StopProcessingAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _results.DisposeAsync();
        await _client.DisposeAsync();
    }
}

/// <summary>
/// Agent side: hosts exactly one agent. Reads only its own queue, rebuilds the context from the
/// message, runs the agent and sends proposals to the results queue. It has no access to platform
/// stores; RBAC lets it receive from its queue and send results, nothing more.
/// </summary>
public sealed class AgentServiceWorker : BackgroundService
{
    private readonly IMatchAgent _agent;
    private readonly AgentBusOptions _options;
    private readonly ILogger<AgentServiceWorker> _logger;

    public AgentServiceWorker(IMatchAgent agent, IOptions<AgentBusOptions> options, ILogger<AgentServiceWorker> logger)
    {
        _agent = agent;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Runs one request. Public so it can be tested without a broker.</summary>
    public static AgentResponse Handle(IMatchAgent agent, AgentRequest request)
    {
        if (request.Agent != agent.Name)
        {
            return new AgentResponse(request.RequestId, agent.Name, [], "request addressed to another agent");
        }

        try
        {
            using var _ = RequestTracking.Use(request.Track);
            var insights = agent.OnEvent(AgentWire.ToContext(request)).Take(Orchestration.AgentCharter.MaxInsightsPerEvent + 1).ToList();
            return new AgentResponse(request.RequestId, agent.Name, insights, null);
        }
        catch (Exception ex)
        {
            return new AgentResponse(request.RequestId, agent.Name, [], ex.GetType().Name);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var client = _options.CreateClient();
        await using var results = client.CreateSender(AgentWire.ResultsQueue);
        // One message at a time keeps event order, which the agents' per-match memory relies on.
        await using var processor = client.CreateProcessor(AgentWire.QueueFor(_agent.Name), new ServiceBusProcessorOptions { MaxConcurrentCalls = 1 });

        processor.ProcessMessageAsync += async args =>
        {
            AgentRequest? request;
            try
            {
                request = args.Message.Body.ToObjectFromJson<AgentRequest>(AgentWire.Json);
            }
            catch (JsonException)
            {
                await args.DeadLetterMessageAsync(args.Message, "malformed", cancellationToken: args.CancellationToken);
                return;
            }

            if (request is null)
            {
                await args.DeadLetterMessageAsync(args.Message, "empty", cancellationToken: args.CancellationToken);
                return;
            }

            var response = Handle(_agent, request);
            await results.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(response, AgentWire.Json))
            {
                MessageId = $"{request.RequestId}-result",
                ContentType = "application/json",
                TimeToLive = TimeSpan.FromSeconds(30),
            }, args.CancellationToken);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        };
        processor.ProcessErrorAsync += e =>
        {
            _logger.LogWarning(e.Exception, "{Agent} receiver error", _agent.Name);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);
        _logger.LogInformation("{Agent} service listening on {Queue}", _agent.Name, AgentWire.QueueFor(_agent.Name));
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }

        await processor.StopProcessingAsync(CancellationToken.None);
    }
}
