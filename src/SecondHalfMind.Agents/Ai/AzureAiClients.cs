using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SecondHalfMind.Agents.Ai;

/// <summary>
/// A language model that turns verified facts into prose. It is given no tools and no data access:
/// it can only return text, which then has to pass the output guard.
/// </summary>
public interface IChatModel
{
    bool IsConfigured { get; }

    Task<string?> CompleteJsonAsync(string systemPrompt, string userContent, CancellationToken cancellationToken);
}

public interface ITranslator
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<string>?> TranslateAsync(IReadOnlyList<string> texts, string targetLanguage, CancellationToken cancellationToken);
}

public interface IContentSafety
{
    bool IsConfigured { get; }

    /// <summary>Returns true when the text is safe to show. Implementations fail closed.</summary>
    Task<bool> IsSafeAsync(string text, CancellationToken cancellationToken);
}

internal static class CognitiveAuth
{
    public const string Resource = "https://cognitiveservices.azure.com";

    public static async Task ApplyAsync(HttpRequestMessage request, string? key, string keyHeader, IAccessTokenProvider tokens, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Add(keyHeader, key);
            return;
        }

        var token = await tokens.GetTokenAsync(Resource, ct);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }
}

/// <summary>Chat completions against an Azure OpenAI model deployment in Microsoft Foundry (REST).</summary>
public sealed class FoundryChatModel : IChatModel
{
    private readonly HttpClient _http;
    private readonly AiOptions _options;
    private readonly IAccessTokenProvider _tokens;
    private readonly ILogger<FoundryChatModel> _logger;

    public FoundryChatModel(HttpClient http, IOptions<AiOptions> options, IAccessTokenProvider tokens, ILogger<FoundryChatModel> logger)
    {
        _http = http;
        _options = options.Value;
        _tokens = tokens;
        _logger = logger;
    }

    public bool IsConfigured =>
        _options.LlmEnabled
        && !string.IsNullOrWhiteSpace(_options.FoundryEndpoint)
        && !string.IsNullOrWhiteSpace(_options.ChatDeployment)
        && (!string.IsNullOrEmpty(_options.FoundryApiKey) || _tokens.IsAvailable);

    public async Task<string?> CompleteJsonAsync(string systemPrompt, string userContent, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var url = $"{_options.FoundryEndpoint!.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(_options.ChatDeployment!)}/chat/completions?api-version={_options.ChatApiVersion}";
        var body = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userContent },
            },
            ["temperature"] = 0.4,
            ["max_tokens"] = 350,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        await CognitiveAuth.ApplyAsync(request, _options.FoundryApiKey, "api-key", _tokens, cancellationToken);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Chat completion failed with status {Status}", (int)response.StatusCode);
                return null;
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var choice = json.RootElement.GetProperty("choices")[0];
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.GetString() == "content_filter")
            {
                return null;
            }

            return choice.GetProperty("message").GetProperty("content").GetString();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Chat completion unavailable; falling back to templates");
            return null;
        }
    }
}

/// <summary>Azure AI Translator (Text Translation v3, REST).</summary>
public sealed class AzureTranslator : ITranslator
{
    private readonly HttpClient _http;
    private readonly AiOptions _options;
    private readonly IAccessTokenProvider _tokens;
    private readonly ILogger<AzureTranslator> _logger;

    public AzureTranslator(HttpClient http, IOptions<AiOptions> options, IAccessTokenProvider tokens, ILogger<AzureTranslator> logger)
    {
        _http = http;
        _options = options.Value;
        _tokens = tokens;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrEmpty(_options.TranslatorKey)
        || (_tokens.IsAvailable && !string.IsNullOrEmpty(_options.TranslatorResourceId));

    public async Task<IReadOnlyList<string>?> TranslateAsync(IReadOnlyList<string> texts, string targetLanguage, CancellationToken cancellationToken)
    {
        if (!IsConfigured || texts.Count == 0)
        {
            return null;
        }

        var url = $"{_options.TranslatorEndpoint.TrimEnd('/')}/translate?api-version=3.0&from=en&to={Uri.EscapeDataString(targetLanguage)}";
        var payload = new JsonArray(texts.Select(t => (JsonNode)new JsonObject { ["Text"] = t }).ToArray());
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        await CognitiveAuth.ApplyAsync(request, _options.TranslatorKey, "Ocp-Apim-Subscription-Key", _tokens, cancellationToken);
        if (!string.IsNullOrEmpty(_options.TranslatorRegion))
        {
            request.Headers.Add("Ocp-Apim-Subscription-Region", _options.TranslatorRegion);
        }

        if (string.IsNullOrEmpty(_options.TranslatorKey) && !string.IsNullOrEmpty(_options.TranslatorResourceId))
        {
            request.Headers.Add("Ocp-Apim-ResourceId", _options.TranslatorResourceId);
        }

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Translation failed with status {Status}", (int)response.StatusCode);
                return null;
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return json.RootElement.EnumerateArray()
                .Select(item => item.GetProperty("translations")[0].GetProperty("text").GetString() ?? string.Empty)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Translator unavailable; showing English");
            return null;
        }
    }
}

/// <summary>Azure AI Content Safety text analysis (REST). Fails closed when configured but unreachable.</summary>
public sealed class AzureContentSafety : IContentSafety
{
    private const int RejectSeverity = 2;
    private readonly HttpClient _http;
    private readonly AiOptions _options;
    private readonly IAccessTokenProvider _tokens;

    public AzureContentSafety(HttpClient http, IOptions<AiOptions> options, IAccessTokenProvider tokens)
    {
        _http = http;
        _options = options.Value;
        _tokens = tokens;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.ContentSafetyEndpoint)
        && (!string.IsNullOrEmpty(_options.ContentSafetyKey) || _tokens.IsAvailable);

    public async Task<bool> IsSafeAsync(string text, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return true;
        }

        var url = $"{_options.ContentSafetyEndpoint!.TrimEnd('/')}/contentsafety/text:analyze?api-version=2024-09-01";
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { text }) };
        await CognitiveAuth.ApplyAsync(request, _options.ContentSafetyKey, "Ocp-Apim-Subscription-Key", _tokens, cancellationToken);
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return json.RootElement.GetProperty("categoriesAnalysis").EnumerateArray()
                .All(c => c.GetProperty("severity").GetInt32() < RejectSeverity);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
            return false;
        }
    }
}
