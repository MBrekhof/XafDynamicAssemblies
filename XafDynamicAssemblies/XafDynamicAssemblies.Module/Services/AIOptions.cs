namespace XafDynamicAssemblies.Module.Services;

public class AIOptions
{
    public string Model { get; set; } = "claude-sonnet-4-6";
    public string DefaultProvider { get; set; } = "anthropic";
    public Dictionary<string, string> ApiKeys { get; set; } = new();
    public int MaxOutputTokens { get; set; } = 16384;
    public int MaxToolIterations { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Test-only escape hatch. When this env var is set to a base URL (e.g.
    /// "http://localhost:5555"), <see cref="TornadoApiProvider"/> and
    /// <see cref="AIChatService"/> route all LLM calls to that endpoint via
    /// LlmTornado's <c>LLmProviders.Custom</c> (unauthenticated, OpenAI-compatible
    /// wire format) instead of a real provider — used to point the app at the
    /// Playwright mock LLM server. Never set outside test runs.
    /// </summary>
    public const string MockLlmBaseUrlEnvVar = "AI_MOCK_LLM_BASE_URL";
}
