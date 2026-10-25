using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using SecondHalfMind.Agents;
using SecondHalfMind.Agents.Ai;
using SecondHalfMind.Agents.Briefing;
using SecondHalfMind.Agents.Guardrails;
using SecondHalfMind.Agents.Hosting;
using SecondHalfMind.Agents.Models;
using SecondHalfMind.Agents.Orchestration;
using SecondHalfMind.Agents.Vision;
using SecondHalfMind.Api;
using SecondHalfMind.Api.Realtime;
using SecondHalfMind.Api.Security;
using SecondHalfMind.Api.Services;
using SecondHalfMind.Data;
using SecondHalfMind.Domain;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(o =>
{
    o.AddServerHeader = false;
    o.Limits.MaxRequestBodySize = 16 * 1024;
});

builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.SectionName));
builder.Services.Configure<EventHubsOptions>(builder.Configuration.GetSection(EventHubsOptions.SectionName));
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();

// ---------------------------------------------------------------------------------------------
// Platform (owns data and its write paths)
// ---------------------------------------------------------------------------------------------
var calibrationPath = builder.Configuration["Calibration:Path"] ?? Path.Combine(AppContext.BaseDirectory, "data", "calibration.json");
var league = new LeagueStore(LeagueStore.LoadCalibration(calibrationPath));
builder.Services.AddSingleton<ILeagueReadModel>(league);

builder.Services.AddSingleton<InMemoryMatchStore>();
builder.Services.AddSingleton<IMatchReadModel>(sp => sp.GetRequiredService<InMemoryMatchStore>());
builder.Services.AddSingleton<IMatchEventWriter>(sp => sp.GetRequiredService<InMemoryMatchStore>());
builder.Services.AddSingleton<IMatchRegistryWriter>(sp => sp.GetRequiredService<InMemoryMatchStore>());
builder.Services.AddSingleton<IMatchEventFeed>(sp => sp.GetRequiredService<InMemoryMatchStore>());

builder.Services.AddSingleton<InsightStore>();
builder.Services.AddSingleton<IInsightSink>(sp => sp.GetRequiredService<InsightStore>());
builder.Services.AddSingleton<IInsightReadModel>(sp => sp.GetRequiredService<InsightStore>());

builder.Services.AddSingleton<PredictionLedger>();
builder.Services.AddSingleton<IFanPredictionReadModel>(sp => sp.GetRequiredService<PredictionLedger>());
builder.Services.AddSingleton<IFanPredictionWriter>(sp => sp.GetRequiredService<PredictionLedger>());

builder.Services.AddSingleton<InMemoryTrackingStore>();
builder.Services.AddSingleton<ITrackingReadModel>(sp => sp.GetRequiredService<InMemoryTrackingStore>());
builder.Services.AddSingleton<ITrackingWriter>(sp => sp.GetRequiredService<InMemoryTrackingStore>());

builder.Services.AddSingleton<IAuditLog>(_ => new InMemoryAuditLog(builder.Configuration["Audit:FilePath"]));
builder.Services.AddSingleton<FanTokens>();

// ---------------------------------------------------------------------------------------------
// Azure AI services (all optional; REST + managed identity, no SDK keys in code)
// ---------------------------------------------------------------------------------------------
// One long-lived HttpClient with pooled-connection recycling (the recommended pattern for singletons).
builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
{
    Timeout = TimeSpan.FromSeconds(15),
});
builder.Services.AddSingleton<IAccessTokenProvider, ManagedIdentityTokenProvider>();
builder.Services.AddSingleton<IChatModel, FoundryChatModel>();
builder.Services.AddSingleton<ITranslator, AzureTranslator>();
builder.Services.AddSingleton<IContentSafety, AzureContentSafety>();
builder.Services.AddSingleton<EventHubsMirror>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EventHubsMirror>());

// ---------------------------------------------------------------------------------------------
// Agents (receive read-only views; their only output is IInsightSink)
// ---------------------------------------------------------------------------------------------
builder.Services.AddSingleton<PreMatchModel>();
builder.Services.AddSingleton<BriefingService>();
builder.Services.AddSingleton<NarrativeComposer>();
// Agents:Mode = InProcess (default) runs agents here; ServiceBus runs each agent as its own service.
var agentBus = builder.Configuration.GetSection(AgentBusOptions.SectionName).Get<AgentBusOptions>() ?? new AgentBusOptions();
builder.Services.Configure<AgentBusOptions>(builder.Configuration.GetSection(AgentBusOptions.SectionName));
if (agentBus.UseServiceBus)
{
    builder.Services.AddRemoteAgents(TimeSpan.FromMilliseconds(agentBus.TimeoutMs));
}
else
{
    builder.Services.AddSingleton<IMatchAgent, StatsAgent>();
    builder.Services.AddSingleton<IMatchAgent, AutoEventingAgent>();
    builder.Services.AddSingleton<IMatchAgent, ExplainerAgent>();
    builder.Services.AddSingleton<IMatchAgent, PredictionRefereeAgent>();
    builder.Services.AddSingleton<IMatchAgent, NarratorAgent>();
    builder.Services.AddSingleton<IMatchAgent, ProducerAgent>();
}

builder.Services.AddSingleton<PersonalizationAgent>();
builder.Services.AddSingleton<MatchOrchestrator>();
builder.Services.AddHostedService<AgentHost>();

// Live data source. Swap for a broadcaster adapter (event feed + optical tracking) without touching agents.
builder.Services.AddSingleton<IMatchFeed, SyntheticMatchFeed>();
builder.Services.AddSingleton<MatchRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MatchRunner>());

// ---------------------------------------------------------------------------------------------
// Realtime, rate limiting, CORS
// ---------------------------------------------------------------------------------------------
builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 8 * 1024;
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
}).AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<RealtimeBroadcaster>();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string Key(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    o.AddPolicy(Endpoints.ReadPolicy, c => RateLimitPartition.GetFixedWindowLimiter(Key(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 240, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(Endpoints.WritePolicy, c => RateLimitPartition.GetFixedWindowLimiter(Key(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
});

var allowedOrigins = builder.Configuration.GetSection("Security:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (allowedOrigins.Length > 0)
    {
        p.WithOrigins(allowedOrigins).AllowAnyHeader().WithMethods("GET", "POST").AllowCredentials();
    }
}));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
}

app.UseSecurityHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();
app.UseRateLimiter();

// Force construction so realtime subscriptions exist before the first match starts.
app.Services.GetRequiredService<RealtimeBroadcaster>();
app.Services.GetRequiredService<PredictionLedger>();

app.MapSecondHalfMind();
app.MapHub<MatchHub>("/hubs/match");
app.MapFallbackToFile("index.html");

SeedFixtures(app.Services, app.Configuration);

app.Run();

static void SeedFixtures(IServiceProvider services, IConfiguration configuration)
{
    var registry = services.GetRequiredService<IMatchRegistryWriter>();
    var fixtures = configuration.GetSection("Demo:Fixtures").Get<string[]>() ?? [];
    var seed = 100;
    foreach (var fixture in fixtures)
    {
        var parts = fixture.Split(':', 2);
        if (parts.Length == 2)
        {
            registry.Register(parts[0], parts[1], seed++);
        }
    }
}

/// <summary>Exposed for integration tests.</summary>
public partial class Program;
