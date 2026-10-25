using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SecondHalfMind.Agents.Remote;
using SecondHalfMind.Agents.Vision;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents.Hosting;

public static class AgentServiceHost
{
    /// <summary>Builds a host that runs one agent behind its own Service Bus queue.</summary>
    public static IHost Build<TAgent>(string[] args)
        where TAgent : class, IMatchAgent
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.Configure<AgentBusOptions>(builder.Configuration.GetSection(AgentBusOptions.SectionName));
        builder.Services.AddSingleton<ITrackingReadModel, RequestTracking>();
        builder.Services.AddSingleton<IMatchAgent, TAgent>();
        builder.Services.AddHostedService<AgentServiceWorker>();
        return builder.Build();
    }

    /// <summary>Platform side: replaces the in-process agents with stand-ins for the six agent services.</summary>
    public static IServiceCollection AddRemoteAgents(this IServiceCollection services, TimeSpan timeout)
    {
        services.AddSingleton<ServiceBusAgentTransport>();
        services.AddSingleton<IRemoteAgentTransport>(sp => sp.GetRequiredService<ServiceBusAgentTransport>());
        services.AddHostedService(sp => sp.GetRequiredService<ServiceBusAgentTransport>());

        foreach (var (name, stage, type) in Catalog)
        {
            services.AddSingleton<IMatchAgent>(sp => new RemoteAgent(name, stage, type, sp.GetRequiredService<IRemoteAgentTransport>(), timeout,
                type == typeof(AutoEventingAgent) ? sp.GetRequiredService<ITrackingReadModel>() : null));
        }

        return services;
    }

    /// <summary>The six agents and the class whose charter each service is held to.</summary>
    public static IReadOnlyList<(string Name, AgentStage Stage, Type Type)> Catalog { get; } =
    [
        ("vision", AgentStage.Perception, typeof(AutoEventingAgent)),
        ("stats", AgentStage.Analysis, typeof(StatsAgent)),
        ("explainer", AgentStage.Interpretation, typeof(ExplainerAgent)),
        ("referee", AgentStage.Interpretation, typeof(PredictionRefereeAgent)),
        ("narrator", AgentStage.Narrative, typeof(NarratorAgent)),
        ("producer", AgentStage.Production, typeof(ProducerAgent)),
    ];
}
