namespace SecondHalfMind.Agents.Ai;

/// <summary>
/// Configuration for Azure AI services. Every service is optional: when it is not configured the
/// agents fall back to deterministic templates, so the app runs fully offline.
/// Prefer managed identity in Azure; API keys are for local development only and must come from
/// user secrets, environment variables or Key Vault, never from source control.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "AI";

    /// <summary>Azure OpenAI endpoint of the model deployment in Microsoft Foundry, e.g. https://my-foundry.openai.azure.com.</summary>
    public string? FoundryEndpoint { get; set; }

    public string? ChatDeployment { get; set; }

    public string ChatApiVersion { get; set; } = "2024-10-21";

    public string? FoundryApiKey { get; set; }

    public string TranslatorEndpoint { get; set; } = "https://api.cognitive.microsofttranslator.com";

    public string? TranslatorKey { get; set; }

    public string? TranslatorRegion { get; set; }

    /// <summary>Resource ID of the Translator resource; required for managed identity auth.</summary>
    public string? TranslatorResourceId { get; set; }

    public string? ContentSafetyEndpoint { get; set; }

    public string? ContentSafetyKey { get; set; }

    public bool UseManagedIdentity { get; set; }

    /// <summary>Client ID of a user-assigned managed identity; leave empty for system-assigned.</summary>
    public string? ManagedIdentityClientId { get; set; }

    public int RequestTimeoutSeconds { get; set; } = 6;

    /// <summary>Hard cap on language model calls per match (cost and blast-radius control).</summary>
    public int MaxLlmCallsPerMatch { get; set; } = 40;

    /// <summary>Global kill switch. When false, no language model is called at all.</summary>
    public bool LlmEnabled { get; set; } = true;
}
