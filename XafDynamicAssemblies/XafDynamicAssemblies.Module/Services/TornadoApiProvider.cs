using LlmTornado;
using LlmTornado.Code;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace XafDynamicAssemblies.Module.Services;

/// <summary>
/// Singleton provider for the shared TornadoApi instance.
/// Separated from AIChatService so that the expensive API initialization
/// happens once, while chat sessions (with per-user history) are scoped.
/// </summary>
public sealed class TornadoApiProvider : IDisposable
{
    private readonly AIOptions _options;
    private readonly ILogger<TornadoApiProvider> _logger;
    private TornadoApi _api;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public TornadoApiProvider(IOptions<AIOptions> optionsAccessor, ILogger<TornadoApiProvider> logger)
    {
        _options = optionsAccessor?.Value ?? new AIOptions();
        _logger = logger;
    }

    public TornadoApi GetApi()
    {
        if (_initialized) return _api;
        _initLock.Wait();
        try
        {
            if (_initialized) return _api;

            var providerKeys = new List<ProviderAuthentication>();
            foreach (var (providerId, apiKey) in _options.ApiKeys)
            {
                if (string.IsNullOrWhiteSpace(apiKey)) continue;
                var provider = MapProvider(providerId);
                if (provider != null)
                    providerKeys.Add(new ProviderAuthentication(provider.Value, apiKey));
            }

            if (providerKeys.Count == 0)
                throw new InvalidOperationException(
                    "No API keys configured. Add at least one provider key to AI:ApiKeys in appsettings.json.");

            _api = new TornadoApi(providerKeys);
            _initialized = true;
            _logger.LogInformation("[TornadoApiProvider] Initialized with {Count} providers", providerKeys.Count);
            return _api;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static LLmProviders? MapProvider(string providerId) => providerId?.ToLowerInvariant() switch
    {
        "anthropic" => LLmProviders.Anthropic,
        "openai" => LLmProviders.OpenAi,
        "google" => LLmProviders.Google,
        "mistral" => LLmProviders.Mistral,
        "cohere" => LLmProviders.Cohere,
        "voyage" => LLmProviders.Voyage,
        "upstage" => LLmProviders.Upstage,
        _ => null
    };

    public void Dispose()
    {
        _initLock.Dispose();
    }
}
