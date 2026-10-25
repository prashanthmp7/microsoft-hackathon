using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SecondHalfMind.Agents.Ai;

public interface IAccessTokenProvider
{
    bool IsAvailable { get; }

    Task<string?> GetTokenAsync(string resource, CancellationToken cancellationToken);
}

/// <summary>
/// Minimal managed identity client for Azure Container Apps and App Service
/// (IDENTITY_ENDPOINT / IDENTITY_HEADER). Keeps the app free of stored secrets in Azure.
/// </summary>
public sealed class ManagedIdentityTokenProvider : IAccessTokenProvider
{
    private readonly HttpClient _http;
    private readonly AiOptions _options;
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresOn)> _cache = new(StringComparer.Ordinal);
    private readonly string? _endpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT");
    private readonly string? _header = Environment.GetEnvironmentVariable("IDENTITY_HEADER");

    public ManagedIdentityTokenProvider(HttpClient http, IOptions<AiOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public bool IsAvailable => _options.UseManagedIdentity && !string.IsNullOrEmpty(_endpoint) && !string.IsNullOrEmpty(_header);

    public async Task<string?> GetTokenAsync(string resource, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return null;
        }

        if (_cache.TryGetValue(resource, out var cached) && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return cached.Token;
        }

        var url = $"{_endpoint}?resource={Uri.EscapeDataString(resource)}&api-version=2019-08-01";
        if (!string.IsNullOrEmpty(_options.ManagedIdentityClientId))
        {
            url += $"&client_id={Uri.EscapeDataString(_options.ManagedIdentityClientId)}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-IDENTITY-HEADER", _header);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var token = json.RootElement.GetProperty("access_token").GetString();
        var expiresOn = json.RootElement.TryGetProperty("expires_on", out var exp)
            && long.TryParse(exp.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : DateTimeOffset.UtcNow.AddMinutes(30);

        if (token is not null)
        {
            _cache[resource] = (token, expiresOn);
        }

        return token;
    }
}
