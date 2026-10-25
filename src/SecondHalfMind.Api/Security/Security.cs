using System.Security.Cryptography;
using System.Text;

namespace SecondHalfMind.Api.Security;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>Operator key for match control (create/start). Store in Key Vault; never commit it.</summary>
    public string? OperatorKey { get; set; }

    /// <summary>
    /// Extra accepted operator keys, for rotation without downtime: add the new key here, move
    /// operators over, then retire the old one. Each key should be a separate Key Vault secret.
    /// </summary>
    public string[] AdditionalOperatorKeys { get; set; } = [];

    /// <summary>HMAC key that signs fan tokens. Key Vault in Azure; a random per-process key if unset.</summary>
    public string? FanTokenKey { get; set; }

    /// <summary>
    /// When true, /audit and /agents are readable without the operator key. Defaults to on in
    /// Development only, so demos work locally while production keeps diagnostics private.
    /// </summary>
    public bool? PublicDiagnostics { get; set; }

    public IEnumerable<string> OperatorKeys =>
        new[] { OperatorKey }.Concat(AdditionalOperatorKeys).Where(k => !string.IsNullOrEmpty(k))!;

    /// <summary>Origins allowed to call the API from a browser (the SPA is same-origin in Azure).</summary>
    public string[] AllowedOrigins { get; set; } = [];
}

public static class SecurityHeaders
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self'; connect-src 'self' ws: wss:; frame-ancestors 'self'; base-uri 'self'; form-action 'self'; object-src 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Frame-Options"] = "SAMEORIGIN";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            headers["Content-Security-Policy"] = ContentSecurityPolicy;
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            await next();
        });
}

/// <summary>
/// Guards match-control endpoints. Uses a constant-time comparison. With no key configured the
/// endpoints are open only in Development; in any other environment they are disabled.
/// </summary>
public sealed class OperatorKeyFilter : IEndpointFilter
{
    public const string HeaderName = "X-Operator-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var options = http.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<SecurityOptions>>().Value;
        var environment = http.RequestServices.GetRequiredService<IHostEnvironment>();
        var keys = options.OperatorKeys.ToList();

        if (keys.Count == 0)
        {
            return environment.IsDevelopment()
                ? await next(context)
                : Results.Problem("Match control is disabled: no operator key configured.", statusCode: StatusCodes.Status403Forbidden);
        }

        return Matches(http.Request.Headers[HeaderName].ToString(), keys)
            ? await next(context)
            : Results.Problem("Invalid operator key.", statusCode: StatusCodes.Status401Unauthorized);
    }

    /// <summary>Constant-time check against every accepted key (no early exit, so timing reveals nothing).</summary>
    public static bool Matches(string supplied, IEnumerable<string> keys)
    {
        var actual = Encoding.UTF8.GetBytes(supplied);
        var ok = false;
        foreach (var key in keys)
        {
            ok |= CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), actual);
        }

        return ok;
    }
}

/// <summary>
/// Guards diagnostics (audit log, agent activity). Open when PublicDiagnostics is on (default:
/// Development only); otherwise requires a valid operator key, and is closed if none is configured.
/// </summary>
public sealed class DiagnosticsFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var options = http.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<SecurityOptions>>().Value;
        var environment = http.RequestServices.GetRequiredService<IHostEnvironment>();
        if (options.PublicDiagnostics ?? environment.IsDevelopment())
        {
            return await next(context);
        }

        var keys = options.OperatorKeys.ToList();
        return keys.Count > 0 && OperatorKeyFilter.Matches(http.Request.Headers[OperatorKeyFilter.HeaderName].ToString(), keys)
            ? await next(context)
            : Results.Problem("Diagnostics require an operator key.", statusCode: StatusCodes.Status401Unauthorized);
    }
}

/// <summary>
/// Server-issued fan identity without accounts. A token is "fanId.signature" where the signature is
/// HMAC-SHA256 over the fan id, so a client can't submit picks or answers as another fan by
/// guessing or copying their id. Tokens carry no personal data.
/// </summary>
public sealed class FanTokens
{
    public const string HeaderName = "X-Fan-Token";

    private readonly byte[] _key;

    public FanTokens(Microsoft.Extensions.Options.IOptions<SecurityOptions> options)
    {
        _key = string.IsNullOrEmpty(options.Value.FanTokenKey)
            ? RandomNumberGenerator.GetBytes(32) // tokens reset on restart; fine locally, set a key in Azure
            : Encoding.UTF8.GetBytes(options.Value.FanTokenKey);
    }

    public (string FanId, string Token) Issue()
    {
        var fanId = Guid.NewGuid().ToString();
        return (fanId, $"{fanId}.{Sign(fanId)}");
    }

    /// <summary>True when the token is genuine and belongs to <paramref name="fanId"/>.</summary>
    public bool Verify(string? token, string fanId)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 200)
        {
            return false;
        }

        var dot = token.IndexOf('.');
        if (dot <= 0 || !string.Equals(token[..dot], fanId, StringComparison.Ordinal))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(fanId));
        var actual = Encoding.ASCII.GetBytes(token[(dot + 1)..]);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private string Sign(string fanId) =>
        Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(fanId)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
