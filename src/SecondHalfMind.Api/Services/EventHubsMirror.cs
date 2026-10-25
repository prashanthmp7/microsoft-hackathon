using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SecondHalfMind.Agents.Ai;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Api.Services;

public sealed class EventHubsOptions
{
    public const string SectionName = "EventHubs";

    /// <summary>Fully qualified namespace, e.g. shm-events.servicebus.windows.net. Empty disables the mirror.</summary>
    public string? Namespace { get; set; }

    public string HubName { get; set; } = "match-events";

    /// <summary>Send-only SAS policy for local development. In Azure, use managed identity (Data Sender role).</summary>
    public string? SasKeyName { get; set; }

    public string? SasKey { get; set; }
}

/// <summary>
/// Mirrors ingested events to Azure Event Hubs (REST, batched) so Microsoft Fabric Real-Time
/// Intelligence can land them in an Eventhouse for KQL analytics. Send-only: the app never reads back
/// or deletes from the hub. Failures are logged and never block the live match.
/// </summary>
public sealed class EventHubsMirror : BackgroundService
{
    private const int BatchSize = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Channel<MatchEvent> _queue = Channel.CreateBounded<MatchEvent>(new BoundedChannelOptions(50_000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly HttpClient _http;
    private readonly EventHubsOptions _options;
    private readonly IAccessTokenProvider _tokens;
    private readonly ILogger<EventHubsMirror> _logger;

    public EventHubsMirror(HttpClient http, IOptions<EventHubsOptions> options, IAccessTokenProvider tokens, ILogger<EventHubsMirror> logger)
    {
        _http = http;
        _options = options.Value;
        _tokens = tokens;
        _logger = logger;
    }

    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(_options.Namespace)
        && (!string.IsNullOrEmpty(_options.SasKey) || _tokens.IsAvailable);

    public void Enqueue(MatchEvent matchEvent)
    {
        if (IsEnabled)
        {
            _queue.Writer.TryWrite(matchEvent);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled)
        {
            _logger.LogInformation("Event Hubs mirror disabled (no namespace or credentials configured)");
            return;
        }

        var batch = new List<MatchEvent>(BatchSize);
        while (await _queue.Reader.WaitToReadAsync(stoppingToken))
        {
            while (batch.Count < BatchSize && _queue.Reader.TryRead(out var e))
            {
                batch.Add(e);
            }

            await SendAsync(batch, stoppingToken);
            batch.Clear();
        }
    }

    private async Task SendAsync(IReadOnlyList<MatchEvent> batch, CancellationToken ct)
    {
        var resource = $"https://{_options.Namespace}/{_options.HubName}";
        var body = JsonSerializer.Serialize(batch.Select(e => new
        {
            Body = JsonSerializer.Serialize(e, Json),
            UserProperties = new { matchId = e.MatchId, type = e.Type.ToString() },
        }));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{resource}/messages?timeout=30&api-version=2014-01");
        request.Content = new StringContent(body, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.microsoft.servicebus.json");

        if (!string.IsNullOrEmpty(_options.SasKey) && !string.IsNullOrEmpty(_options.SasKeyName))
        {
            request.Headers.TryAddWithoutValidation("Authorization", CreateSasToken(resource, _options.SasKeyName, _options.SasKey, TimeSpan.FromMinutes(30)));
        }
        else
        {
            var token = await _tokens.GetTokenAsync("https://eventhubs.azure.net", ct);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Event Hubs batch rejected with status {Status}", (int)response.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Event Hubs unreachable; batch of {Count} dropped from mirror", batch.Count);
        }
    }

    internal static string CreateSasToken(string resourceUri, string keyName, string key, TimeSpan lifetime)
    {
        var expiry = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var encodedUri = Uri.EscapeDataString(resourceUri);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{encodedUri}\n{expiry}")));
        return $"SharedAccessSignature sr={encodedUri}&sig={Uri.EscapeDataString(signature)}&se={expiry}&skn={keyName}";
    }
}
