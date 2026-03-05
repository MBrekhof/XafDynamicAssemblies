using System.Text.Json;
using LlmTornado;
using LlmTornado.Chat;
using LlmTornado.Chat.Models;
using LlmTornado.ChatFunctions;
using LlmTornado.Code;
using LlmTornado.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace XafDynamicAssemblies.Module.Services;

/// <summary>
/// Scoped chat service — each Blazor circuit gets its own instance with
/// independent conversation history. The expensive TornadoApi is shared
/// via the singleton <see cref="TornadoApiProvider"/>.
/// </summary>
public sealed class AIChatService : IDisposable
{
    private readonly AIOptions _options;
    private readonly ILogger<AIChatService> _logger;
    private readonly TornadoApiProvider _apiProvider;
    private readonly SchemaDiscoveryService _discoveryService;

    private readonly List<ChatMessageEntry> _history = new();
    private const int MaxHistoryMessages = 50;

    public string CurrentModel
    {
        get => _options.Model;
        set => _options.Model = value;
    }

    /// <summary>
    /// LLMTornado Tool definitions for the LLM to know what tools are available.
    /// </summary>
    public IReadOnlyList<Tool> TornadoTools { get; set; }

    /// <summary>
    /// AIFunction instances for executing tool calls by name.
    /// </summary>
    public IReadOnlyList<AIFunction> ToolFunctions { get; set; }

    /// <summary>
    /// System message — refreshed before each conversation turn with current metadata.
    /// </summary>
    public string SystemMessage { get; set; }

    public AIChatService(
        IOptions<AIOptions> optionsAccessor,
        ILogger<AIChatService> logger,
        TornadoApiProvider apiProvider,
        SchemaDiscoveryService discoveryService)
    {
        _options = optionsAccessor?.Value ?? new AIOptions();
        _logger = logger;
        _apiProvider = apiProvider ?? throw new ArgumentNullException(nameof(apiProvider));
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
    }

    public async Task<string> AskAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var api = _apiProvider.GetApi();

        // Refresh system prompt with current entity metadata
        RefreshSystemPrompt();

        var provider = ResolveProvider(_options.Model);
        var pipeline = CreateRetryPipeline(cancellationToken);
        int toolIterations = 0;

        var response = await pipeline.ExecuteAsync(async ct =>
        {
            var chatRequest = new ChatRequest
            {
                Model = new ChatModel(_options.Model, provider),
                MaxTokens = _options.MaxOutputTokens,
                Temperature = 1.0
            };

            if (TornadoTools is { Count: > 0 })
                chatRequest.Tools = TornadoTools.ToList();

            var conversation = api.Chat.CreateConversation(chatRequest);

            // System prompt
            if (!string.IsNullOrWhiteSpace(SystemMessage))
                conversation.AppendSystemMessage(SystemMessage);

            // Replay conversation history for continuity
            foreach (var entry in _history)
            {
                if (entry.Role == "user")
                    conversation.AppendUserInput(entry.Content);
                else
                    conversation.AppendExampleChatbotOutput(entry.Content);
            }

            // Current user message
            conversation.AppendUserInput(prompt);

            _logger.LogInformation(
                "[AskAsync] Sending (model={Model}, provider={Provider}, tools={Tools}, history={History})",
                _options.Model, provider, TornadoTools?.Count ?? 0, _history.Count);

            // Tool-calling loop: GetResponseRich populates tool results in the conversation
            // but does NOT automatically re-send to the LLM. We loop until no more tool calls.
            ChatRichResponse richResponse = null;
            bool hasToolCalls = true;

            while (hasToolCalls && toolIterations < _options.MaxToolIterations)
            {
                richResponse = await conversation.GetResponseRich(async functionCalls =>
                {
                    toolIterations++;
                    _logger.LogInformation("[ToolLoop] Iteration {Iter}: {Count} tool call(s)",
                        toolIterations, functionCalls.Count);

                    foreach (var fc in functionCalls)
                    {
                        var result = await ExecuteToolAsync(fc.Name, fc.Arguments ?? "{}");
                        _logger.LogInformation("[ToolLoop] {Name} -> {ResultLen} chars", fc.Name, result.Length);
                        fc.Result = new FunctionResult(fc, result);
                    }
                }, ct);

                // Check if the response still contains unresolved tool calls
                hasToolCalls = richResponse?.Blocks?.Any(b =>
                    b.Type == ChatRichResponseBlockTypes.Function && b.FunctionCall != null) == true;

                if (hasToolCalls)
                    _logger.LogInformation("[ToolLoop] Response still has tool calls, continuing loop");
            }

            if (toolIterations >= _options.MaxToolIterations)
                _logger.LogWarning("[ToolLoop] Hit max iterations ({Max})", _options.MaxToolIterations);

            return richResponse;
        }, cancellationToken);

        // Extract text from response
        var finalText = string.Empty;
        if (response?.Blocks != null)
        {
            var textParts = new List<string>();
            foreach (var block in response.Blocks)
            {
                if (block.Type == ChatRichResponseBlockTypes.Message && block.Message != null)
                    textParts.Add(block.Message);
            }
            finalText = string.Join("\n", textParts);
        }

        // Fallback to simple text property
        if (string.IsNullOrEmpty(finalText) && response != null)
            finalText = response.Text ?? string.Empty;

        // Update conversation history
        _history.Add(new ChatMessageEntry("user", prompt));
        if (!string.IsNullOrEmpty(finalText))
            _history.Add(new ChatMessageEntry("assistant", finalText));

        // Trim history to prevent unbounded growth (remove in pairs)
        while (_history.Count > MaxHistoryMessages * 2)
        {
            _history.RemoveAt(0);
            _history.RemoveAt(0);
        }

        // Log token usage if available
        if (response?.Usage != null)
        {
            _logger.LogInformation("[AskAsync] Tokens - input: {In}, output: {Out}",
                response.Usage.PromptTokens, response.Usage.CompletionTokens);
        }

        _logger.LogInformation("[AskAsync] Response: {Len} chars, {Iterations} tool iterations",
            finalText.Length, toolIterations);

        return string.IsNullOrEmpty(finalText)
            ? "No response received from the AI model. Please try again."
            : finalText;
    }

    /// <summary>
    /// Clears conversation history.
    /// </summary>
    public void ClearHistory() => _history.Clear();

    /// <summary>
    /// Refreshes the system prompt with current runtime entity metadata.
    /// </summary>
    private void RefreshSystemPrompt()
    {
        try
        {
            var runtimeTypes = XafDynamicAssemblies.Module.BusinessObjects.XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes;
            var runtimeTypeNames = new HashSet<string>(runtimeTypes.Select(t => t.Name));

            // Build summary list — we don't have DB access here so use what we can infer
            // from the live runtime types. The tools themselves have full DB access.
            var summaries = runtimeTypes.Select(t => new CustomClassSummary
            {
                ClassName = t.Name,
                FieldCount = t.GetProperties().Count(p => p.DeclaringType == t),
                Status = BusinessObjects.CustomClassStatus.Runtime,
                IsDeployed = true
            }).ToList();

            SystemMessage = _discoveryService.GenerateSystemPrompt(summaries);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RefreshSystemPrompt] Failed to refresh, keeping existing prompt");
        }
    }

    private async Task<string> ExecuteToolAsync(string toolName, string argumentsJson)
    {
        if (ToolFunctions == null) return "Error: No tools registered.";

        var function = ToolFunctions.FirstOrDefault(f => f.Name == toolName);
        if (function == null) return $"Error: Unknown tool '{toolName}'.";

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(argumentsJson)
                       ?? new Dictionary<string, object>();

            var args = new AIFunctionArguments(dict);
            var result = await function.InvokeAsync(args);
            return result?.ToString() ?? "Tool returned no result.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ExecuteTool] {Name} failed", toolName);
            return $"Error executing {toolName}: {ex.Message}";
        }
    }

    /// <summary>
    /// Creates a resilience pipeline with retry (excluding user cancellations) and timeout.
    /// </summary>
    private ResiliencePipeline CreateRetryPipeline(CancellationToken userCancellationToken)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(2),
                UseJitter = true,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex =>
                {
                    // Do not retry when the user explicitly cancelled
                    if (ex is OperationCanceledException oce && userCancellationToken.IsCancellationRequested)
                        return false;

                    // Retry timeouts (TaskCanceledException with no user cancellation)
                    if (ex is TaskCanceledException or OperationCanceledException) return true;

                    if (ex is HttpRequestException httpEx)
                    {
                        var status = (int)(httpEx.StatusCode ?? 0);
                        return status == 429 || status >= 500;
                    }
                    return false;
                }),
                OnRetry = args =>
                {
                    _logger.LogWarning(args.Outcome.Exception,
                        "[Retry] Attempt {Attempt}/3 for model {Model}, retrying in {Delay:F1}s",
                        args.AttemptNumber + 1, _options.Model, args.RetryDelay.TotalSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(_options.TimeoutSeconds))
            .Build();
    }

    private LLmProviders ResolveProvider(string modelId)
    {
        if (modelId.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) return LLmProviders.Anthropic;
        if (modelId.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)) return LLmProviders.OpenAi;
        if (modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase)) return LLmProviders.OpenAi;
        if (modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase)) return LLmProviders.OpenAi;
        if (modelId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)) return LLmProviders.Google;
        if (modelId.StartsWith("mistral", StringComparison.OrdinalIgnoreCase)) return LLmProviders.Mistral;

        return MapProvider(_options.DefaultProvider) ?? LLmProviders.Anthropic;
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

    public void Dispose() { }

    private sealed record ChatMessageEntry(string Role, string Content);
}
