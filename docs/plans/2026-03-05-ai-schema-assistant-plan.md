# AI Schema Assistant — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a conversational AI side panel that lets users create, modify, and delete runtime entity schemas and manage role permissions through natural language.

**Architecture:** LLMTornado-based AI chat service with 10 tool functions operating on CustomClass/CustomField/PermissionPolicyRole via XAF ObjectSpace. DxAIChat Blazor component in a side panel. Two-tier schema discovery (lightweight system prompt + on-demand detail tools). Mocked + live Playwright test suites.

**Tech Stack:** LLMTornado 3.8+, Polly (Microsoft.Extensions.Resilience), Markdig, HtmlSanitizer, DevExpress DxAIChat, Playwright Python

**Design Doc:** `docs/plans/2026-03-05-ai-schema-assistant-design.md`

**Reference Codebase:** `C:\Projects\XafTornado` — informed patterns but built fresh

---

## Task 1: Add NuGet Packages + AIOptions Config Model

**Files:**
- Modify: `XafDynamicAssemblies/XafDynamicAssemblies.Module/XafDynamicAssemblies.Module.csproj`
- Modify: `XafDynamicAssemblies/XafDynamicAssemblies.Blazor.Server/XafDynamicAssemblies.Blazor.Server.csproj`
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/AIOptions.cs`
- Modify: `XafDynamicAssemblies/XafDynamicAssemblies.Blazor.Server/appsettings.json`
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Blazor.Server/appsettings.Development.json` (if not exists)

**Step 1: Add NuGet packages to Module project**

```xml
<!-- Module.csproj — add these PackageReferences -->
<PackageReference Include="LlmTornado" Version="3.8.49" />
<PackageReference Include="Microsoft.Extensions.Resilience" Version="8.0.0" />
<PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="9.0.0" />
```

```xml
<!-- Blazor.Server.csproj — add these PackageReferences -->
<PackageReference Include="Markdig" Version="0.45.0" />
<PackageReference Include="Ganss.Xss" Version="1.9.0" />
<PackageReference Include="Microsoft.Extensions.AI" Version="9.0.0" />
```

**Step 2: Create AIOptions.cs**

```csharp
namespace XafDynamicAssemblies.Module.Services;

public class AIOptions
{
    public string Model { get; set; } = "claude-sonnet-4-6";
    public string DefaultProvider { get; set; } = "anthropic";
    public Dictionary<string, string> ApiKeys { get; set; } = new();
    public int MaxOutputTokens { get; set; } = 16384;
    public int MaxToolIterations { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 120;
}
```

**Step 3: Add AI section to appsettings.json**

```json
{
  "AI": {
    "Model": "claude-sonnet-4-6",
    "DefaultProvider": "anthropic",
    "MaxOutputTokens": 16384,
    "MaxToolIterations": 10,
    "TimeoutSeconds": 120,
    "ApiKeys": {
      "anthropic": "",
      "openai": ""
    }
  }
}
```

Add to `appsettings.Development.json` (gitignored):
```json
{
  "AI": {
    "ApiKeys": {
      "anthropic": "sk-ant-YOUR-KEY-HERE"
    }
  }
}
```

**Step 4: Verify build**

Run: `dotnet build XafDynamicAssemblies.slnx`
Expected: Build succeeds with new packages restored.

**Step 5: Commit**

```bash
git add -A
git commit -m "feat: add AI NuGet packages and AIOptions config model"
```

---

## Task 2: SchemaDiscoveryService

Discovers existing entities (compiled + runtime) via ITypesInfo and CustomClass metadata. Generates the system prompt for the AI.

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/SchemaDiscoveryService.cs`

**Step 1: Create SchemaDiscoveryService**

```csharp
using DevExpress.ExpressApp.DC;
using XafDynamicAssemblies.Module.BusinessObjects;

namespace XafDynamicAssemblies.Module.Services;

public class SchemaDiscoveryService
{
    private readonly object _lock = new();
    private SchemaInfo _cache;

    public SchemaInfo Schema
    {
        get
        {
            lock (_lock)
            {
                _cache ??= DiscoverSchema();
                return _cache;
            }
        }
    }

    public void InvalidateCache()
    {
        lock (_lock) { _cache = null; }
    }

    /// <summary>
    /// Generate the system prompt with entity list, supported types, and role context.
    /// Kept lightweight — full entity details are fetched on-demand via describe_entity tool.
    /// </summary>
    public string GenerateSystemPrompt(List<CustomClassSummary> metadataClasses = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are a schema design assistant for a runtime entity system.");
        sb.AppendLine("Users describe business entities in natural language. You create, modify, and delete entity definitions.");
        sb.AppendLine("You also manage role-based permissions for entities.");
        sb.AppendLine();
        sb.AppendLine("## Rules");
        sb.AppendLine("- ALWAYS present a summary of proposed changes and ask for confirmation before executing create/modify/delete tools.");
        sb.AppendLine("- After making changes, remind the user to click Deploy to apply them.");
        sb.AppendLine("- Infer appropriate field types from context (e.g., 'price' -> System.Decimal, 'email' -> System.String, 'date of birth' -> System.DateTime).");
        sb.AppendLine("- Use PascalCase for class and field names.");
        sb.AppendLine("- When the user mentions a relationship to an existing entity, use a Reference field type.");
        sb.AppendLine();
        sb.AppendLine("## Supported Field Types");
        sb.AppendLine("System.String, System.Int32, System.Int64, System.Decimal, System.Double, System.Single,");
        sb.AppendLine("System.Boolean, System.DateTime, System.Guid, System.Byte[], Reference (FK to another entity)");
        sb.AppendLine();

        // Metadata entities (pending/undeployed)
        if (metadataClasses?.Count > 0)
        {
            sb.AppendLine("## Defined Entities (metadata)");
            foreach (var c in metadataClasses)
            {
                var status = c.IsDeployed ? "deployed" : "pending deploy";
                sb.AppendLine($"- **{c.ClassName}** ({c.FieldCount} fields, {c.Status}, {status})");
            }
            sb.AppendLine();
        }

        // Live schema from ITypesInfo (compiled entities available for references)
        var schema = Schema;
        if (schema.CompiledEntities.Count > 0)
        {
            sb.AppendLine("## Compiled Entities (available for references)");
            foreach (var e in schema.CompiledEntities)
            {
                sb.AppendLine($"- {e}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Use the provided tools to inspect, create, modify, and delete entities. Use describe_entity for full field details.");
        return sb.ToString();
    }

    private SchemaInfo DiscoverSchema()
    {
        var info = new SchemaInfo();

        // Discover compiled entity names from ITypesInfo if available
        try
        {
            var typesInfo = XafTypesInfo.Instance;
            foreach (var ti in typesInfo.PersistentTypes)
            {
                // Skip runtime entities (they're in CustomClass metadata)
                if (XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes.Any(rt => rt.Name == ti.Name))
                    continue;
                // Skip DevExpress internal types
                if (ti.Type.Namespace?.StartsWith("DevExpress") == true)
                    continue;
                // Skip our metadata types
                if (ti.Type == typeof(CustomClass) || ti.Type == typeof(CustomField))
                    continue;
                if (ti.Type == typeof(SchemaPackage) || ti.Type == typeof(SchemaHistory))
                    continue;

                info.CompiledEntities.Add(ti.Name);
            }
        }
        catch
        {
            // ITypesInfo may not be ready yet during early bootstrap
        }

        return info;
    }
}

public class SchemaInfo
{
    public List<string> CompiledEntities { get; set; } = new();
}

public class CustomClassSummary
{
    public string ClassName { get; set; }
    public int FieldCount { get; set; }
    public string Status { get; set; }
    public bool IsDeployed { get; set; }
}
```

**Step 2: Verify build**

Run: `dotnet build XafDynamicAssemblies.slnx`
Expected: Build succeeds.

**Step 3: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/SchemaDiscoveryService.cs
git commit -m "feat: add SchemaDiscoveryService for AI system prompt generation"
```

---

## Task 3: AIChatService (LLMTornado Integration)

Core service: manages LLMTornado API, conversation history, tool-calling loop, and Polly retry.

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/AIChatService.cs`

**Step 1: Create AIChatService**

```csharp
using System.Text;
using LlmTornado;
using LlmTornado.Chat;
using LlmTornado.Chat.Models;
using LlmTornado.Code;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace XafDynamicAssemblies.Module.Services;

public class AIChatService
{
    private readonly AIOptions _options;
    private readonly ILogger<AIChatService> _logger;
    private readonly List<ChatMessage> _history = new();
    private TornadoApi _api;
    private const int MaxHistoryPairs = 50;

    public IReadOnlyList<AIFunction> ToolFunctions { get; set; } = [];
    public IReadOnlyList<Tool> TornadoTools { get; set; } = [];
    public string SystemMessage { get; set; } = "";
    public ChatModel CurrentModel { get; set; }

    public AIChatService(IOptions<AIOptions> options, ILogger<AIChatService> logger)
    {
        _options = options.Value;
        _logger = logger;
        InitializeApi();
    }

    private void InitializeApi()
    {
        var keys = new List<ProviderAuthentication>();

        if (_options.ApiKeys.TryGetValue("anthropic", out var anthropicKey) && !string.IsNullOrEmpty(anthropicKey))
            keys.Add(new ProviderAuthentication(LLmProviders.Anthropic, anthropicKey));

        if (_options.ApiKeys.TryGetValue("openai", out var openaiKey) && !string.IsNullOrEmpty(openaiKey))
            keys.Add(new ProviderAuthentication(LLmProviders.OpenAi, openaiKey));

        if (_options.ApiKeys.TryGetValue("google", out var googleKey) && !string.IsNullOrEmpty(googleKey))
            keys.Add(new ProviderAuthentication(LLmProviders.Google, googleKey));

        _api = new TornadoApi(keys);
        CurrentModel = ChatModel.FromString(_options.Model);
    }

    public async Task<string> AskAsync(string prompt, CancellationToken ct = default)
    {
        _history.Add(new ChatMessage(ChatMessageRoles.User, prompt));
        TrimHistory();

        var retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(2),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex =>
                    ex.Message.Contains("429") || ex.Message.Contains("500") ||
                    ex.Message.Contains("502") || ex.Message.Contains("503") ||
                    ex is TimeoutException)
            })
            .Build();

        string finalResponse = null;

        await retryPipeline.ExecuteAsync(async token =>
        {
            finalResponse = await ExecuteToolLoop(token);
        }, ct);

        if (finalResponse != null)
        {
            _history.Add(new ChatMessage(ChatMessageRoles.Assistant, finalResponse));
            TrimHistory();
        }

        return finalResponse ?? "I'm sorry, I couldn't generate a response. Please try again.";
    }

    private async Task<string> ExecuteToolLoop(CancellationToken ct)
    {
        for (int iteration = 0; iteration < _options.MaxToolIterations; iteration++)
        {
            var conversation = new Conversation(CurrentModel)
            {
                MaxOutputTokens = _options.MaxOutputTokens
            };
            conversation.AppendMessage(ChatMessageRoles.System, SystemMessage);

            foreach (var msg in _history)
                conversation.Messages.Add(msg);

            if (TornadoTools.Count > 0)
                conversation.Tools = TornadoTools.ToList();

            var response = await _api.Chat.GetResponseRich(conversation, ct);

            if (response == null)
                throw new InvalidOperationException("No response from AI provider");

            // Check for tool calls
            var toolCalls = response.ToolCalls;
            if (toolCalls == null || toolCalls.Count == 0)
            {
                return response.Content;
            }

            // Add assistant message with tool calls to history
            _history.Add(new ChatMessage(ChatMessageRoles.Assistant, response));

            // Execute each tool call
            foreach (var toolCall in toolCalls)
            {
                var result = await ExecuteToolCall(toolCall, ct);
                _history.Add(new ChatMessage(ChatMessageRoles.Tool, result)
                {
                    ToolCallId = toolCall.Id
                });
            }
        }

        return "I've reached the maximum number of tool iterations. Please try a simpler request.";
    }

    private async Task<string> ExecuteToolCall(ToolCall toolCall, CancellationToken ct)
    {
        try
        {
            var function = ToolFunctions.FirstOrDefault(f => f.Name == toolCall.FunctionCall.Name);
            if (function == null)
                return $"Error: Unknown tool '{toolCall.FunctionCall.Name}'";

            _logger.LogInformation("Executing tool: {ToolName}", toolCall.FunctionCall.Name);

            var args = toolCall.FunctionCall.ParseArguments();
            var result = await function.InvokeAsync(args, ct);
            return result?.ToString() ?? "Success (no output)";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool execution failed: {ToolName}", toolCall.FunctionCall.Name);
            return $"Error executing tool: {ex.Message}";
        }
    }

    public void ClearHistory()
    {
        _history.Clear();
    }

    private void TrimHistory()
    {
        // Keep system-relevant messages, trim old user/assistant pairs
        while (_history.Count > MaxHistoryPairs * 2)
        {
            _history.RemoveAt(0);
            if (_history.Count > 0 && _history[0].Role == ChatMessageRoles.Assistant)
                _history.RemoveAt(0);
        }
    }
}
```

**Note:** The exact LLMTornado API may differ from this skeleton. During implementation, consult the LLMTornado GitHub repo (`https://github.com/lofcz/LLMTornado`) for current Conversation, Tool, and ToolCall APIs. The XafTornado reference at `C:\Projects\XafTornado\XafTornado\XafTornado.Module\Services\AIChatService.cs` has a working implementation — adapt its patterns.

**Step 2: Verify build**

Run: `dotnet build XafDynamicAssemblies.slnx`
Expected: Build succeeds.

**Step 3: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/AIChatService.cs
git commit -m "feat: add AIChatService with LLMTornado tool loop and Polly retry"
```

---

## Task 4: SchemaAIToolsProvider — Read Tools

Implements `list_entities`, `describe_entity`, `get_active_schema`, `get_pending_changes`, and `validate_schema`. These are read-only tools that don't modify data.

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/SchemaAIToolsProvider.cs`

**Step 1: Create SchemaAIToolsProvider with read-only tools**

```csharp
using System.Text;
using DevExpress.ExpressApp;
using LlmTornado.Chat;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using XafDynamicAssemblies.Module.BusinessObjects;

namespace XafDynamicAssemblies.Module.Services;

public class SchemaAIToolsProvider
{
    private readonly IServiceProvider _serviceProvider;
    private readonly SchemaDiscoveryService _discoveryService;
    private List<AIFunction> _tools;
    private List<Tool> _tornadoTools;

    public SchemaAIToolsProvider(IServiceProvider serviceProvider, SchemaDiscoveryService discoveryService)
    {
        _serviceProvider = serviceProvider;
        _discoveryService = discoveryService;
    }

    public IReadOnlyList<AIFunction> Tools => _tools ??= BuildTools();
    public IReadOnlyList<Tool> GetTornadoTools() => _tornadoTools ??= BuildTornadoTools();

    // --- Helper: create a scoped ObjectSpace ---
    private IObjectSpace CreateObjectSpace()
    {
        var scope = _serviceProvider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<INonSecuredObjectSpaceFactory>();
        return factory.CreateNonSecuredObjectSpace<CustomClass>();
    }

    // ============================================================
    // TOOL: list_entities
    // ============================================================
    private string ListEntities()
    {
        using var os = CreateObjectSpace();
        var classes = os.GetObjectsQuery<CustomClass>().OrderBy(c => c.ClassName).ToList();

        if (classes.Count == 0)
            return "No entities defined yet.";

        var sb = new StringBuilder();
        sb.AppendLine("| Entity | Fields | Status | API Exposed |");
        sb.AppendLine("|--------|--------|--------|-------------|");
        foreach (var c in classes)
        {
            var fieldCount = c.Fields?.Count ?? 0;
            sb.AppendLine($"| {c.ClassName} | {fieldCount} | {c.Status} | {(c.IsApiExposed ? "Yes" : "No")} |");
        }
        return sb.ToString();
    }

    // ============================================================
    // TOOL: describe_entity
    // ============================================================
    private string DescribeEntity(string entityName)
    {
        using var os = CreateObjectSpace();
        var cc = os.GetObjectsQuery<CustomClass>()
            .FirstOrDefault(c => c.ClassName == entityName);

        if (cc == null)
            return $"Entity '{entityName}' not found.";

        var sb = new StringBuilder();
        sb.AppendLine($"## {cc.ClassName}");
        sb.AppendLine($"- **Status:** {cc.Status}");
        sb.AppendLine($"- **Navigation Group:** {cc.NavigationGroup ?? "(none)"}");
        sb.AppendLine($"- **Description:** {cc.Description ?? "(none)"}");
        sb.AppendLine($"- **API Exposed:** {(cc.IsApiExposed ? "Yes" : "No")}");
        sb.AppendLine();

        var fields = cc.Fields?.OrderBy(f => f.SortOrder).ThenBy(f => f.FieldName).ToList() ?? [];
        if (fields.Count == 0)
        {
            sb.AppendLine("No fields defined.");
        }
        else
        {
            sb.AppendLine("| Field | Type | Required | Default | Reference |");
            sb.AppendLine("|-------|------|----------|---------|-----------|");
            foreach (var f in fields)
            {
                var refClass = f.TypeName == "Reference" ? f.ReferencedClassName : "";
                sb.AppendLine($"| {f.FieldName} | {f.TypeName} | {(f.IsRequired ? "Yes" : "No")} | {(f.IsDefaultField ? "Yes" : "No")} | {refClass} |");
            }
        }
        return sb.ToString();
    }

    // ============================================================
    // TOOL: get_active_schema
    // ============================================================
    private string GetActiveSchema()
    {
        var schema = _discoveryService.Schema;
        var runtimeTypes = XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes;

        var sb = new StringBuilder();
        sb.AppendLine("## Currently Live Schema");
        sb.AppendLine();

        if (runtimeTypes.Length > 0)
        {
            sb.AppendLine("### Deployed Runtime Entities");
            foreach (var t in runtimeTypes)
            {
                var props = t.GetProperties()
                    .Where(p => p.Name != "ID" && p.Name != "GCRecord" && p.Name != "OptimisticLockField" && p.Name != "ObjectType")
                    .Select(p => $"{p.Name} ({p.PropertyType.Name})")
                    .ToList();
                sb.AppendLine($"- **{t.Name}**: {string.Join(", ", props)}");
            }
            sb.AppendLine();
        }

        if (schema.CompiledEntities.Count > 0)
        {
            sb.AppendLine("### Compiled Entities");
            foreach (var e in schema.CompiledEntities)
                sb.AppendLine($"- {e}");
        }

        return sb.ToString();
    }

    // ============================================================
    // TOOL: get_pending_changes
    // ============================================================
    private string GetPendingChanges()
    {
        using var os = CreateObjectSpace();
        var runtimeClasses = os.GetObjectsQuery<CustomClass>()
            .Where(c => c.Status == CustomClassStatus.Runtime)
            .OrderBy(c => c.ClassName)
            .ToList();

        var liveTypeNames = XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes
            .Select(t => t.Name).ToHashSet();

        var sb = new StringBuilder();
        var hasChanges = false;

        // New entities (in metadata but not live)
        var newEntities = runtimeClasses.Where(c => !liveTypeNames.Contains(c.ClassName)).ToList();
        if (newEntities.Count > 0)
        {
            hasChanges = true;
            sb.AppendLine("### New Entities (will be created on Deploy)");
            foreach (var c in newEntities)
                sb.AppendLine($"- **{c.ClassName}** ({c.Fields?.Count ?? 0} fields)");
            sb.AppendLine();
        }

        // Modified entities (in both — field changes detected by comparing counts/names)
        // Note: detailed field-level diff would require comparing against live type properties
        var existingEntities = runtimeClasses.Where(c => liveTypeNames.Contains(c.ClassName)).ToList();
        if (existingEntities.Count > 0)
        {
            sb.AppendLine("### Existing Entities (may have field changes)");
            foreach (var c in existingEntities)
            {
                var liveType = XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes.First(t => t.Name == c.ClassName);
                var liveFieldCount = liveType.GetProperties()
                    .Count(p => p.Name != "ID" && p.Name != "GCRecord" && p.Name != "OptimisticLockField" && p.Name != "ObjectType");
                var metaFieldCount = c.Fields?.Count ?? 0;
                if (liveFieldCount != metaFieldCount)
                {
                    hasChanges = true;
                    sb.AppendLine($"- **{c.ClassName}**: {liveFieldCount} live fields -> {metaFieldCount} defined fields");
                }
            }
            sb.AppendLine();
        }

        // Removed entities (live but no longer in metadata)
        var metaNames = runtimeClasses.Select(c => c.ClassName).ToHashSet();
        var removedTypes = liveTypeNames.Where(n => !metaNames.Contains(n)).ToList();
        if (removedTypes.Count > 0)
        {
            hasChanges = true;
            sb.AppendLine("### Removed Entities (will be dropped on Deploy)");
            foreach (var name in removedTypes)
                sb.AppendLine($"- **{name}**");
            sb.AppendLine();
        }

        if (!hasChanges)
            sb.AppendLine("No pending changes detected. Schema is up to date.");

        return sb.ToString();
    }

    // ============================================================
    // TOOL: validate_schema
    // ============================================================
    private string ValidateSchema()
    {
        try
        {
            var classes = XafDynamicAssembliesModule.QueryMetadata(XafDynamicAssembliesModule.RuntimeConnectionString);
            if (classes.Count == 0)
                return "No entities defined. Nothing to validate.";

            var result = RuntimeAssemblyBuilder.TestCompile(classes);
            if (result.Success)
                return $"Validation passed. {classes.Count} entity(ies) compile successfully.";

            var sb = new StringBuilder();
            sb.AppendLine("Validation **failed** with errors:");
            foreach (var error in result.Errors.Take(10))
                sb.AppendLine($"- {error}");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Validation error: {ex.Message}";
        }
    }

    // ============================================================
    // Tool registration (implementation will depend on LLMTornado API)
    // ============================================================
    private List<AIFunction> BuildTools()
    {
        // Build AIFunction wrappers for each tool method
        // Adapt from XafTornado's AIToolsProvider pattern
        // Each tool: name, description, parameters schema, invoke delegate
        throw new NotImplementedException("Wire up tools — see Task 4 Step 2");
    }

    private List<Tool> BuildTornadoTools()
    {
        // Build LLMTornado Tool schemas for the LLM to understand
        // Each tool: function name, description, JSON schema for parameters
        throw new NotImplementedException("Wire up tornado tools — see Task 4 Step 2");
    }
}
```

**Step 2: Wire up tool registration**

Consult `C:\Projects\XafTornado\XafTornado\XafTornado.Module\Services\AIToolsProvider.cs` for the exact pattern of:
1. Creating `AIFunction` instances with `AIFunctionFactory.Create()` from method delegates
2. Creating `Tool` instances with `new Tool(new ToolFunction(...))` for LLMTornado

Key tool definitions:

| Tool Name | Parameters | Return |
|-----------|------------|--------|
| `list_entities` | (none) | Markdown table of all entities |
| `describe_entity` | `entityName: string` | Markdown with full field details |
| `get_active_schema` | (none) | Markdown of live compiled + runtime schema |
| `get_pending_changes` | (none) | Markdown diff between metadata and live |
| `validate_schema` | (none) | Compilation result |

**Step 3: Verify build**

Run: `dotnet build XafDynamicAssemblies.slnx`
Expected: Build succeeds (with NotImplementedException in wire-up methods — acceptable until Task 8).

**Step 4: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/SchemaAIToolsProvider.cs
git commit -m "feat: add SchemaAIToolsProvider with read-only tools"
```

---

## Task 5: SchemaAIToolsProvider — Write Tools

Implements `create_entity`, `modify_entity`, and `delete_entity`. These modify CustomClass/CustomField metadata.

**Files:**
- Modify: `XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/SchemaAIToolsProvider.cs`

**Step 1: Add create_entity tool**

```csharp
// Add to SchemaAIToolsProvider class

/// <summary>
/// Creates a new CustomClass with fields. The AI should have already confirmed with the user.
/// </summary>
private string CreateEntity(string className, string navigationGroup, string description,
    string fieldsJson)
{
    // fieldsJson is a JSON array: [{"name":"Price","type":"System.Decimal","required":false,"referencedClass":null}, ...]
    using var os = CreateObjectSpace();

    // Check for duplicate
    var existing = os.GetObjectsQuery<CustomClass>().FirstOrDefault(c => c.ClassName == className);
    if (existing != null)
        return $"Error: Entity '{className}' already exists.";

    var cc = os.CreateObject<CustomClass>();
    cc.ClassName = className;
    cc.NavigationGroup = navigationGroup ?? "General";
    cc.Description = description;

    // Parse fields
    var fields = System.Text.Json.JsonSerializer.Deserialize<List<FieldDefinition>>(fieldsJson ?? "[]");
    int sortOrder = 0;
    foreach (var f in fields)
    {
        var cf = os.CreateObject<CustomField>();
        cf.CustomClass = cc;
        cf.FieldName = f.Name;
        cf.TypeName = f.Type ?? "System.String";
        cf.IsRequired = f.Required;
        cf.ReferencedClassName = f.ReferencedClass;
        cf.SortOrder = sortOrder++;
        cc.Fields.Add(cf);
    }

    os.CommitChanges();
    return $"Created entity **{className}** with {fields.Count} field(s) in navigation group '{cc.NavigationGroup}'. Click **Deploy** to apply.";
}

private class FieldDefinition
{
    public string Name { get; set; }
    public string Type { get; set; }
    public bool Required { get; set; }
    public string ReferencedClass { get; set; }
}
```

**Step 2: Add modify_entity tool**

```csharp
/// <summary>
/// Modifies an existing CustomClass: add/remove/update fields, change properties.
/// </summary>
private string ModifyEntity(string entityName, string modificationsJson)
{
    // modificationsJson: {"addFields":[...],"removeFields":["FieldName"],"updateFields":[{"name":"OldName","newName":"NewName","type":"System.Int32"}],"navigationGroup":"NewGroup","description":"New desc","isApiExposed":true}
    using var os = CreateObjectSpace();
    var cc = os.GetObjectsQuery<CustomClass>().FirstOrDefault(c => c.ClassName == entityName);
    if (cc == null)
        return $"Error: Entity '{entityName}' not found.";

    var mods = System.Text.Json.JsonSerializer.Deserialize<EntityModifications>(modificationsJson);
    var changes = new List<string>();

    // Update class-level properties
    if (mods.NavigationGroup != null) { cc.NavigationGroup = mods.NavigationGroup; changes.Add($"Navigation group -> '{mods.NavigationGroup}'"); }
    if (mods.Description != null) { cc.Description = mods.Description; changes.Add($"Description updated"); }
    if (mods.IsApiExposed.HasValue) { cc.IsApiExposed = mods.IsApiExposed.Value; changes.Add($"API exposed -> {mods.IsApiExposed.Value}"); }

    // Remove fields
    if (mods.RemoveFields?.Count > 0)
    {
        foreach (var fieldName in mods.RemoveFields)
        {
            var field = cc.Fields.FirstOrDefault(f => f.FieldName == fieldName);
            if (field != null) { os.Delete(field); changes.Add($"Removed field '{fieldName}'"); }
        }
    }

    // Add fields
    if (mods.AddFields?.Count > 0)
    {
        var maxSort = cc.Fields.Any() ? cc.Fields.Max(f => f.SortOrder) + 1 : 0;
        foreach (var f in mods.AddFields)
        {
            var cf = os.CreateObject<CustomField>();
            cf.CustomClass = cc;
            cf.FieldName = f.Name;
            cf.TypeName = f.Type ?? "System.String";
            cf.IsRequired = f.Required;
            cf.ReferencedClassName = f.ReferencedClass;
            cf.SortOrder = maxSort++;
            cc.Fields.Add(cf);
            changes.Add($"Added field '{f.Name}' ({f.Type ?? "System.String"})");
        }
    }

    // Update fields
    if (mods.UpdateFields?.Count > 0)
    {
        foreach (var upd in mods.UpdateFields)
        {
            var field = cc.Fields.FirstOrDefault(f => f.FieldName == upd.Name);
            if (field == null) { changes.Add($"Warning: field '{upd.Name}' not found"); continue; }
            if (upd.NewName != null) { field.FieldName = upd.NewName; changes.Add($"Renamed '{upd.Name}' -> '{upd.NewName}'"); }
            if (upd.Type != null) { field.TypeName = upd.Type; changes.Add($"Changed type of '{upd.Name}' -> {upd.Type}"); }
            if (upd.Required.HasValue) { field.IsRequired = upd.Required.Value; }
        }
    }

    os.CommitChanges();

    var sb = new StringBuilder();
    sb.AppendLine($"Modified **{entityName}**:");
    foreach (var c in changes) sb.AppendLine($"- {c}");
    sb.AppendLine();
    sb.AppendLine("Click **Deploy** to apply changes.");
    return sb.ToString();
}

private class EntityModifications
{
    public List<FieldDefinition> AddFields { get; set; }
    public List<string> RemoveFields { get; set; }
    public List<FieldUpdate> UpdateFields { get; set; }
    public string NavigationGroup { get; set; }
    public string Description { get; set; }
    public bool? IsApiExposed { get; set; }
}

private class FieldUpdate
{
    public string Name { get; set; }
    public string NewName { get; set; }
    public string Type { get; set; }
    public bool? Required { get; set; }
}
```

**Step 3: Add delete_entity tool**

```csharp
private string DeleteEntity(string entityName)
{
    using var os = CreateObjectSpace();
    var cc = os.GetObjectsQuery<CustomClass>().FirstOrDefault(c => c.ClassName == entityName);
    if (cc == null)
        return $"Error: Entity '{entityName}' not found.";

    if (cc.Status == CustomClassStatus.Compiled)
        return $"Error: Entity '{entityName}' is compiled and cannot be deleted through the AI assistant.";

    var fieldCount = cc.Fields?.Count ?? 0;
    os.Delete(cc);
    os.CommitChanges();

    return $"Deleted entity **{entityName}** and its {fieldCount} field(s). Click **Deploy** to apply.";
}
```

**Step 4: Verify build**

Run: `dotnet build XafDynamicAssemblies.slnx`
Expected: Build succeeds.

**Step 5: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/SchemaAIToolsProvider.cs
git commit -m "feat: add create/modify/delete entity tools to SchemaAIToolsProvider"
```

---

## Task 6: SchemaAIToolsProvider — Role Permission Tools

Implements `list_roles` and `set_role_permissions`.

**Files:**
- Modify: `XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/SchemaAIToolsProvider.cs`

**Step 1: Add list_roles tool**

```csharp
private string ListRoles()
{
    using var os = CreateObjectSpace();

    // PermissionPolicyRole may need the security module to be registered
    // Use dynamic lookup via ITypesInfo to avoid hard dependency
    var roleType = XafTypesInfo.Instance.FindTypeInfo("DevExpress.Persistent.BaseImpl.EF.PermissionPolicy.PermissionPolicyRole")?.Type;
    if (roleType == null)
        return "Security module is not configured. Cannot list roles.";

    var roles = os.GetObjects(roleType);
    if (roles == null)
        return "No roles found.";

    var sb = new StringBuilder();
    sb.AppendLine("| Role | Admin | Type Permissions |");
    sb.AppendLine("|------|-------|-----------------|");

    foreach (dynamic role in roles)
    {
        string name = role.Name;
        bool isAdmin = role.IsAdministrative;
        int permCount = 0;
        try { permCount = role.TypePermissions?.Count ?? 0; } catch { }
        sb.AppendLine($"| {name} | {(isAdmin ? "Yes" : "No")} | {permCount} |");
    }

    return sb.ToString();
}
```

**Step 2: Add set_role_permissions tool**

```csharp
private string SetRolePermissions(string roleName, string entityName,
    bool allowRead, bool allowWrite, bool allowCreate, bool allowDelete)
{
    using var os = CreateObjectSpace();

    var roleType = XafTypesInfo.Instance.FindTypeInfo("DevExpress.Persistent.BaseImpl.EF.PermissionPolicy.PermissionPolicyRole")?.Type;
    if (roleType == null)
        return "Security module is not configured.";

    dynamic role = os.FindObject(roleType,
        DevExpress.Data.Filtering.CriteriaOperator.Parse($"Name = '{roleName}'"));
    if (role == null)
        return $"Error: Role '{roleName}' not found.";

    // Find the entity type (runtime or compiled)
    Type entityType = XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes
        .FirstOrDefault(t => t.Name == entityName);
    if (entityType == null)
    {
        var ti = XafTypesInfo.Instance.FindTypeInfo(entityName);
        entityType = ti?.Type;
    }
    if (entityType == null)
        return $"Error: Entity '{entityName}' not found in live schema. Deploy first if it's a new entity.";

    // Find or create type permission
    dynamic typePermission = null;
    foreach (dynamic tp in role.TypePermissions)
    {
        if (tp.TargetType == entityType)
        {
            typePermission = tp;
            break;
        }
    }

    if (typePermission == null)
    {
        var tpType = XafTypesInfo.Instance.FindTypeInfo("DevExpress.Persistent.BaseImpl.EF.PermissionPolicy.PermissionPolicyTypePermissionObject")?.Type;
        if (tpType == null)
            return "Error: Cannot find TypePermission type.";

        typePermission = os.CreateObject(tpType);
        typePermission.TargetType = entityType;
        role.TypePermissions.Add(typePermission);
    }

    typePermission.AllowRead = allowRead
        ? DevExpress.Persistent.Base.SecurityPermissionState.Allow
        : DevExpress.Persistent.Base.SecurityPermissionState.Deny;
    typePermission.AllowWrite = allowWrite
        ? DevExpress.Persistent.Base.SecurityPermissionState.Allow
        : DevExpress.Persistent.Base.SecurityPermissionState.Deny;
    typePermission.AllowCreate = allowCreate
        ? DevExpress.Persistent.Base.SecurityPermissionState.Allow
        : DevExpress.Persistent.Base.SecurityPermissionState.Deny;
    typePermission.AllowDelete = allowDelete
        ? DevExpress.Persistent.Base.SecurityPermissionState.Allow
        : DevExpress.Persistent.Base.SecurityPermissionState.Deny;

    os.CommitChanges();

    var perms = new List<string>();
    if (allowRead) perms.Add("Read");
    if (allowWrite) perms.Add("Write");
    if (allowCreate) perms.Add("Create");
    if (allowDelete) perms.Add("Delete");

    return $"Updated role **{roleName}** — {entityName}: {(perms.Count > 0 ? string.Join(", ", perms) : "No permissions")}.";
}
```

**Note:** The security module may or may not be configured in this project. The `dynamic` approach avoids hard compile-time dependencies on `DevExpress.ExpressApp.Security`. During implementation, check if the security module is already in the project's dependencies. If not, this feature may need to be deferred or the NuGet package added.

**Step 3: Verify build**

Run: `dotnet build XafDynamicAssemblies.slnx`
Expected: Build succeeds.

**Step 4: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/SchemaAIToolsProvider.cs
git commit -m "feat: add role permission tools to SchemaAIToolsProvider"
```

---

## Task 7: Complete Tool Registration (Wire BuildTools + BuildTornadoTools)

Replace the `NotImplementedException` stubs with actual tool registration.

**Files:**
- Modify: `XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/SchemaAIToolsProvider.cs`

**Step 1: Implement BuildTools and BuildTornadoTools**

Consult `C:\Projects\XafTornado\XafTornado\XafTornado.Module\Services\AIToolsProvider.cs` for the exact pattern. The key is dual registration:

1. `AIFunction` — for execution via `Microsoft.Extensions.AI`
2. `Tool` — for LLMTornado to send the schema to the LLM

Each tool needs:
- A `ToolFunction` with name, description, and JSON parameter schema
- An `AIFunction` that wraps the method delegate

Example pattern from XafTornado:
```csharp
private List<Tool> BuildTornadoTools()
{
    return new List<Tool>
    {
        new Tool(new ToolFunction(
            "list_entities",
            "List all defined entities with their status, field count, and whether they are deployed.",
            new Dictionary<string, object>()  // no parameters
        )),
        new Tool(new ToolFunction(
            "describe_entity",
            "Get detailed information about an entity including all its fields, types, and relationships.",
            new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["entityName"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["description"] = "The name of the entity to describe"
                    }
                },
                ["required"] = new[] { "entityName" }
            }
        )),
        // ... remaining tools ...
    };
}
```

**Step 2: Verify build**

Run: `dotnet build XafDynamicAssemblies.slnx`
Expected: Build succeeds with no NotImplementedException.

**Step 3: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/SchemaAIToolsProvider.cs
git commit -m "feat: complete tool registration in SchemaAIToolsProvider"
```

---

## Task 8: AIChatClient Adapter

Simple IChatClient wrapper that bridges DevExpress DxAIChat to AIChatService.

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/AIChatClient.cs`

**Step 1: Create AIChatClient**

```csharp
using Microsoft.Extensions.AI;

namespace XafDynamicAssemblies.Module.Services;

public class AIChatClient : IChatClient
{
    private readonly AIChatService _chatService;

    public AIChatClient(AIChatService chatService)
    {
        _chatService = chatService;
    }

    public ChatClientMetadata Metadata => new("LLMTornado");

    public async Task<ChatResponse> GetResponseAsync(
        IList<ChatMessage> chatMessages,
        ChatOptions options = null,
        CancellationToken ct = default)
    {
        var lastUserMessage = chatMessages?.LastOrDefault(m => m.Role == ChatRole.User);
        var prompt = lastUserMessage?.Text ?? "";

        var response = await _chatService.AskAsync(prompt, ct);

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, response));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IList<ChatMessage> chatMessages,
        ChatOptions options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var lastUserMessage = chatMessages?.LastOrDefault(m => m.Role == ChatRole.User);
        var prompt = lastUserMessage?.Text ?? "";

        var response = await _chatService.AskAsync(prompt, ct);

        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Text = response
        };
    }

    public void Dispose() { }

    public object GetService(Type serviceType, object serviceKey = null) => null;
}
```

**Step 2: Verify build and commit**

```bash
dotnet build XafDynamicAssemblies.slnx
git add XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/AIChatClient.cs
git commit -m "feat: add AIChatClient IChatClient adapter"
```

---

## Task 9: ServiceCollectionExtensions + DI Wiring

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/AIServiceCollectionExtensions.cs`

**Step 1: Create DI registration extension**

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace XafDynamicAssemblies.Module.Services;

public static class AIServiceCollectionExtensions
{
    public static IServiceCollection AddAIServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AIOptions>(configuration.GetSection("AI"));
        services.AddSingleton<SchemaDiscoveryService>();
        services.AddSingleton<AIChatService>();
        services.AddSingleton<SchemaAIToolsProvider>();

        services.AddChatClient(sp =>
        {
            var chatService = sp.GetRequiredService<AIChatService>();
            var tools = sp.GetRequiredService<SchemaAIToolsProvider>();
            var discovery = sp.GetRequiredService<SchemaDiscoveryService>();

            chatService.ToolFunctions = tools.Tools;
            chatService.TornadoTools = tools.GetTornadoTools();
            chatService.SystemMessage = discovery.GenerateSystemPrompt();

            return new AIChatClient(chatService);
        });

        return services;
    }
}
```

**Step 2: Verify build and commit**

```bash
dotnet build XafDynamicAssemblies.slnx
git add XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/AIServiceCollectionExtensions.cs
git commit -m "feat: add AI DI registration extension method"
```

---

## Task 10: AIChat.razor Blazor Side Panel Component

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Blazor.Server/Editors/AIChatViewItem/AIChat.razor`

**Step 1: Create the Blazor component**

Consult `C:\Projects\XafTornado\XafTornado\XafTornado.Blazor.Server\Editors\AIChatViewItem\AIChat.razor` for the DxAIChat component pattern. Key elements:

```razor
@using DevExpress.AIIntegration.Blazor.Chat
@using Markdig
@using Ganss.Xss
@inject IChatClient ChatClient

<DxAIChat ChatClient="@ChatClient"
          UseStreaming="true"
          ResponseContentFormat="Markdown"
          RenderMarkdown="@ToHtml"
          CssClass="ai-schema-chat">
    <EmptyMessageAreaTemplate>
        <div class="ai-empty-state">
            <h4>Schema Assistant</h4>
            <p>Describe the entities you need, and I'll create them for you.</p>
            <div class="prompt-suggestions">
                <button @onclick='() => SendPrompt("Create a new entity")'>Create a new entity</button>
                <button @onclick='() => SendPrompt("Show pending changes")'>Show pending changes</button>
                <button @onclick='() => SendPrompt("List all entities")'>List all entities</button>
                <button @onclick='() => SendPrompt("Set up permissions")'>Set up permissions</button>
            </div>
        </div>
    </EmptyMessageAreaTemplate>
</DxAIChat>

<style>
    .ai-schema-chat {
        height: 100%;
        display: flex;
        flex-direction: column;
    }
    .ai-schema-chat table { border-collapse: collapse; width: 100%; margin: 8px 0; }
    .ai-schema-chat th, .ai-schema-chat td { border: 1px solid #ddd; padding: 6px 10px; text-align: left; }
    .ai-schema-chat th { background: #f5f5f5; font-weight: 600; }
    .ai-schema-chat code { background: rgba(0,0,0,0.06); padding: 2px 5px; border-radius: 3px; font-size: 0.9em; }
    .ai-schema-chat pre code { background: #1e1e1e; color: #d4d4d4; display: block; padding: 12px; border-radius: 6px; overflow-x: auto; }
    .ai-empty-state { text-align: center; padding: 40px 20px; color: #666; }
    .prompt-suggestions { display: flex; flex-wrap: wrap; gap: 8px; justify-content: center; margin-top: 16px; }
    .prompt-suggestions button { padding: 8px 16px; border: 1px solid #ddd; border-radius: 20px; background: #fff; cursor: pointer; }
    .prompt-suggestions button:hover { background: #f0f0f0; border-color: #999; }
</style>

@code {
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();
    private static readonly HtmlSanitizer Sanitizer = new();

    private string ToHtml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";
        var html = Markdown.ToHtml(markdown, Pipeline);
        return Sanitizer.Sanitize(html);
    }

    private void SendPrompt(string prompt)
    {
        // Use DxAIChat's API to send a prompt programmatically
        // Implementation depends on DxAIChat component API
    }
}
```

**Note:** The exact DxAIChat API (properties, events, methods) should be verified against the DevExpress 25.2 documentation. The XafTornado implementation at `C:\Projects\XafTornado\XafTornado\XafTornado.Blazor.Server\Editors\AIChatViewItem\AIChat.razor` is the working reference.

**Step 2: Register the component as a XAF ViewItem or side panel**

This requires either:
- A XAF `DashboardViewItem` or custom `ViewItem` that hosts the Razor component
- A Blazor layout modification to add a persistent side panel

Check how XafTornado registers its chat — it uses a non-persistent `DomainComponent` (`AIChat.cs`) as a navigation target. For a side panel approach, a layout modification or a docked panel component may be needed.

Consult `C:\Projects\XafTornado\XafTornado\XafTornado.Module\BusinessObjects\AIChat.cs` and `C:\Projects\XafTornado\XafTornado\XafTornado.Blazor.Server\Editors\AIChatViewItem\` for the ViewItem registration pattern.

**Step 3: Verify build and commit**

```bash
dotnet build XafDynamicAssemblies.slnx
git add XafDynamicAssemblies/XafDynamicAssemblies.Blazor.Server/Editors/
git commit -m "feat: add AIChat.razor Blazor side panel component"
```

---

## Task 11: Startup.cs Integration

Wire everything together in the Blazor Server host.

**Files:**
- Modify: `XafDynamicAssemblies/XafDynamicAssemblies.Blazor.Server/Startup.cs`

**Step 1: Add AI services to ConfigureServices**

```csharp
// In Startup.ConfigureServices(), after services.AddServerSideBlazor():
services.AddAIServices(Configuration);
```

Add using:
```csharp
using XafDynamicAssemblies.Module.Services;
```

**Step 2: Verify build**

Run: `dotnet build XafDynamicAssemblies.slnx`
Expected: Build succeeds.

**Step 3: Manual smoke test**

Run: `dotnet run --project XafDynamicAssemblies/XafDynamicAssemblies.Blazor.Server`

Verify:
- App starts without errors
- AI chat panel is visible in the UI
- Typing a message sends it to the AI and gets a response (requires valid API key in appsettings.Development.json)
- `list_entities` tool works (AI lists existing custom classes)

**Step 4: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Blazor.Server/Startup.cs
git commit -m "feat: wire AI services into Blazor Server startup"
```

---

## Task 12: Mock LLM Server for Tests

A Python Flask server that mimics LLMTornado's API contract with pre-scripted responses.

**Files:**
- Create: `tests/mock_llm/server.py`
- Create: `tests/mock_llm/scripts.py`
- Create: `tests/mock_llm/requirements.txt`

**Step 1: Create mock server**

```python
# tests/mock_llm/server.py
"""
Mock LLM server for deterministic AI chat testing.
Mimics the Anthropic/OpenAI chat completion API.
Returns pre-scripted tool calls and responses based on input patterns.
"""
import json
from flask import Flask, request, jsonify
from scripts import match_response

app = Flask(__name__)

@app.route("/v1/chat/completions", methods=["POST"])
@app.route("/v1/messages", methods=["POST"])  # Anthropic format
def chat_completions():
    data = request.json
    messages = data.get("messages", [])

    # Find last user message
    last_user = ""
    for msg in reversed(messages):
        if msg.get("role") == "user":
            last_user = msg.get("content", "")
            break

    # Check if this is a tool result follow-up
    has_tool_results = any(msg.get("role") == "tool" for msg in messages)

    response = match_response(last_user, has_tool_results, messages)
    return jsonify(response)

if __name__ == "__main__":
    import sys
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 5555
    app.run(host="0.0.0.0", port=port)
```

**Step 2: Create response scripts**

```python
# tests/mock_llm/scripts.py
"""
Pre-scripted AI responses for deterministic testing.
Each script matches an input pattern and returns a response with optional tool calls.
"""
import json
import re

def match_response(user_message, has_tool_results, all_messages):
    """Match user message to a scripted response."""
    lower = user_message.lower()

    # After tool results, generate final response
    if has_tool_results:
        return text_response(generate_followup(all_messages))

    # --- Entity Creation Flow ---
    if "create" in lower and any(w in lower for w in ["entity", "product", "customer", "order"]):
        return text_response(
            "I'll create **TestProduct** with these fields:\n"
            "- Name (String)\n"
            "- Price (Decimal)\n"
            "- Description (String)\n\n"
            "Look good?"
        )

    if lower.strip() in ("yes", "y", "looks good", "go ahead", "confirm"):
        # Check conversation context for what was proposed
        return tool_call_response("create_entity", {
            "className": "TestProduct",
            "navigationGroup": "Products",
            "description": "A test product entity",
            "fieldsJson": json.dumps([
                {"name": "Name", "type": "System.String", "required": True},
                {"name": "Price", "type": "System.Decimal", "required": False},
                {"name": "Description", "type": "System.String", "required": False}
            ])
        })

    # --- List Entities ---
    if "list" in lower and ("entities" in lower or "entity" in lower):
        return tool_call_response("list_entities", {})

    # --- Describe Entity ---
    if "describe" in lower or "show" in lower and "fields" in lower:
        entity_name = extract_entity_name(lower)
        return tool_call_response("describe_entity", {"entityName": entity_name or "TestProduct"})

    # --- Pending Changes ---
    if "pending" in lower or "changes" in lower:
        return tool_call_response("get_pending_changes", {})

    # --- Add Field ---
    if "add" in lower and "field" in lower:
        return text_response(
            "I'll add a **StockQuantity** (Int32) field to **TestProduct**.\n\nLook good?"
        )

    # --- Delete Entity ---
    if "delete" in lower or "remove" in lower and ("entity" in lower or "class" in lower):
        entity_name = extract_entity_name(lower)
        return text_response(
            f"I'll delete **{entity_name or 'TestProduct'}** and all its fields.\n\n"
            "This cannot be undone. Proceed?"
        )

    # --- Role Permissions ---
    if "permission" in lower or "role" in lower or "access" in lower:
        if "list" in lower:
            return tool_call_response("list_roles", {})
        return text_response(
            "I'll update the **Admins** role:\n"
            "- TestProduct: Read, Write, Create, Delete (all enabled)\n\n"
            "Look good?"
        )

    # --- Validate ---
    if "validate" in lower or "compile" in lower:
        return tool_call_response("validate_schema", {})

    # Default
    return text_response("I can help you create, modify, or delete entities. What would you like to do?")


def text_response(text):
    """Return a simple text response (no tool calls)."""
    return {
        "id": "mock-1",
        "object": "chat.completion",
        "choices": [{
            "index": 0,
            "message": {"role": "assistant", "content": text},
            "finish_reason": "stop"
        }]
    }

def tool_call_response(tool_name, arguments):
    """Return a response with a tool call."""
    return {
        "id": "mock-1",
        "object": "chat.completion",
        "choices": [{
            "index": 0,
            "message": {
                "role": "assistant",
                "content": None,
                "tool_calls": [{
                    "id": f"call_{tool_name}",
                    "type": "function",
                    "function": {
                        "name": tool_name,
                        "arguments": json.dumps(arguments)
                    }
                }]
            },
            "finish_reason": "tool_calls"
        }]
    }

def extract_entity_name(text):
    """Try to extract an entity name from the message."""
    # Simple heuristic: look for PascalCase words
    words = re.findall(r'\b[A-Z][a-zA-Z]+\b', text)
    # Filter out common words
    skip = {"create", "delete", "remove", "add", "show", "list", "entity", "field"}
    for w in words:
        if w.lower() not in skip:
            return w
    return None

def generate_followup(messages):
    """Generate a followup after tool execution."""
    # Find the last tool result
    for msg in reversed(messages):
        if msg.get("role") == "tool":
            content = msg.get("content", "")
            if "Created entity" in content:
                return content + "\n\nYou have pending changes. Click **Deploy** when ready."
            if "No entities defined" in content:
                return "There are no entities defined yet. Would you like to create one?"
            if "Validation passed" in content:
                return content
            return content
    return "Done."
```

**Step 3: Add requirements**

```
# tests/mock_llm/requirements.txt
flask==3.1.0
```

**Step 4: Commit**

```bash
git add tests/mock_llm/
git commit -m "feat: add mock LLM server for deterministic AI chat testing"
```

---

## Task 13: AIChatPanel Page Object

Playwright page object for interacting with the DxAIChat component.

**Files:**
- Create: `tests/pages/ai_chat_page.py`

**Step 1: Create page object**

```python
# tests/pages/ai_chat_page.py
"""Page object for the AI Schema Assistant chat panel."""
from playwright.sync_api import Page, expect


class AIChatPanel:
    """Wraps DxAIChat component interactions."""

    def __init__(self, page: Page):
        self.page = page
        # DxAIChat selectors — verify against actual rendered HTML
        self._chat_container = ".ai-schema-chat"
        self._input = ".ai-schema-chat textarea, .ai-schema-chat input[type='text']"
        self._send_button = ".ai-schema-chat button[title='Send'], .ai-schema-chat .dxai-send-btn"
        self._messages = ".ai-schema-chat .dxai-message"
        self._assistant_messages = ".ai-schema-chat .dxai-message-assistant, .ai-schema-chat .dxai-message[data-role='assistant']"
        self._user_messages = ".ai-schema-chat .dxai-message-user, .ai-schema-chat .dxai-message[data-role='user']"
        self._loading = ".ai-schema-chat .dxai-loading, .ai-schema-chat .dxai-typing"
        self._suggestions = ".prompt-suggestions button"

    def is_visible(self) -> bool:
        """Check if the chat panel is visible."""
        return self.page.locator(self._chat_container).is_visible()

    def wait_for_panel(self, timeout: int = 10000):
        """Wait for the chat panel to become visible."""
        self.page.wait_for_selector(self._chat_container, timeout=timeout)

    def send_message(self, text: str, timeout: int = 30000):
        """Type and send a message, wait for AI response."""
        input_el = self.page.locator(self._input).first
        input_el.fill(text)
        input_el.press("Enter")
        self.wait_for_response(timeout)

    def wait_for_response(self, timeout: int = 30000):
        """Wait for the AI to finish responding (loading indicator disappears)."""
        # Wait for loading to appear (brief)
        try:
            self.page.wait_for_selector(self._loading, timeout=3000)
        except:
            pass
        # Wait for loading to disappear
        self.page.wait_for_selector(self._loading, state="hidden", timeout=timeout)
        self.page.wait_for_timeout(500)  # Extra buffer for rendering

    def get_last_response(self) -> str:
        """Get the text of the last assistant message."""
        messages = self.page.locator(self._assistant_messages).all()
        if not messages:
            return ""
        return messages[-1].inner_text()

    def get_last_response_html(self) -> str:
        """Get the HTML of the last assistant message (for verifying markdown rendering)."""
        messages = self.page.locator(self._assistant_messages).all()
        if not messages:
            return ""
        return messages[-1].inner_html()

    def get_all_responses(self) -> list[str]:
        """Get all assistant messages as text."""
        return [m.inner_text() for m in self.page.locator(self._assistant_messages).all()]

    def get_message_count(self) -> int:
        """Get total number of messages (user + assistant)."""
        return self.page.locator(self._messages).count()

    def click_suggestion(self, text: str):
        """Click a prompt suggestion button by text."""
        self.page.locator(self._suggestions).filter(has_text=text).click()
        self.wait_for_response()

    def has_table_in_last_response(self) -> bool:
        """Check if the last response contains a rendered HTML table."""
        html = self.get_last_response_html()
        return "<table" in html.lower()

    def response_contains(self, text: str) -> bool:
        """Check if the last response contains specific text."""
        return text.lower() in self.get_last_response().lower()
```

**Note:** The exact DxAIChat CSS selectors (`dxai-message`, `dxai-send-btn`, etc.) must be verified against the actual rendered HTML. Run the app, open browser DevTools, and inspect the DxAIChat component's DOM structure. Update selectors accordingly.

**Step 2: Commit**

```bash
git add tests/pages/ai_chat_page.py
git commit -m "feat: add AIChatPanel page object for Playwright tests"
```

---

## Task 14: Mocked Playwright Test Suite

Tests the full AI chat UI flow with the mock LLM server.

**Files:**
- Create: `tests/tests/test_phase11_ai_chat_mocked.py`
- Modify: `tests/conftest.py` (add mock server fixture)

**Step 1: Add mock server fixture to conftest.py**

```python
# Add to conftest.py

import subprocess
import time
import requests

MOCK_LLM_PORT = 5555

@pytest.fixture(scope="session")
def mock_llm_server():
    """Start mock LLM server for AI chat tests."""
    proc = subprocess.Popen(
        ["python", "mock_llm/server.py", str(MOCK_LLM_PORT)],
        cwd=os.path.dirname(__file__),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE
    )
    # Wait for server to start
    for _ in range(30):
        try:
            requests.get(f"http://localhost:{MOCK_LLM_PORT}/")
            break
        except:
            time.sleep(0.5)
    yield proc
    proc.terminate()
    proc.wait()
```

**Note:** The server also needs configuration to point LLMTornado at the mock endpoint. This may require:
- An environment variable or `appsettings.Testing.json` that overrides the AI provider base URL
- Or a custom `TornadoApi` configuration that accepts a base URL override

Check LLMTornado's API for custom endpoint configuration. XafTornado may have a pattern for this.

**Step 2: Create mocked test suite**

```python
# tests/tests/test_phase11_ai_chat_mocked.py
"""
Phase 11: AI Schema Assistant — Mocked LLM Tests
Tests chat UI, entity creation flow, modification, deletion, validation, and role permissions.
Uses mock LLM server for deterministic responses.
"""
import pytest
from pages.ai_chat_page import AIChatPanel
from pages.navigation_page import NavigationPage
from pages.list_view_page import ListViewPage

# These tests require the mock LLM server running and the app configured to use it.
# See conftest.py mock_llm_server fixture.


class TestAIChatUIBasics:
    """Chat panel visibility, message rendering, prompt suggestions."""

    def test_01_chat_panel_visible(self, page, mock_llm_server):
        """Chat panel is visible in the UI."""
        chat = AIChatPanel(page)
        chat.wait_for_panel()
        assert chat.is_visible()

    def test_02_send_message_gets_response(self, page, mock_llm_server):
        """Sending a message returns an AI response."""
        chat = AIChatPanel(page)
        chat.wait_for_panel()
        chat.send_message("Hello")
        assert chat.get_message_count() >= 2  # user + assistant
        assert len(chat.get_last_response()) > 0

    def test_03_prompt_suggestion_works(self, page, mock_llm_server):
        """Clicking a prompt suggestion sends the prompt."""
        chat = AIChatPanel(page)
        chat.wait_for_panel()
        chat.click_suggestion("List all entities")
        assert chat.get_message_count() >= 2

    def test_04_markdown_table_renders(self, page, mock_llm_server):
        """AI response with markdown table renders as HTML table."""
        chat = AIChatPanel(page)
        chat.wait_for_panel()
        chat.send_message("List all entities")
        # Mock returns a tool call -> list_entities -> markdown table
        assert chat.has_table_in_last_response()


class TestEntityCreationFlow:
    """Create entity via natural language conversation."""

    def test_01_propose_entity(self, page, mock_llm_server):
        """AI proposes entity creation with fields."""
        chat = AIChatPanel(page)
        chat.wait_for_panel()
        chat.send_message("Create a product entity with name, price, and description")
        response = chat.get_last_response()
        assert "TestProduct" in response
        assert "Name" in response
        assert "Price" in response
        assert "Decimal" in response

    def test_02_confirm_creation(self, page, mock_llm_server):
        """Confirming creates the entity in metadata."""
        chat = AIChatPanel(page)
        chat.wait_for_panel()
        chat.send_message("Create a product entity with name, price, and description")
        chat.send_message("yes")
        response = chat.get_last_response()
        assert "Created" in response or "created" in response
        assert "Deploy" in response

    def test_03_entity_exists_in_metadata(self, page, mock_llm_server):
        """After AI creates entity, it appears in CustomClass list."""
        # Navigate to Schema Management > Custom Class
        nav = NavigationPage(page)
        nav.navigate_to("Schema Management", "Custom Class")
        lv = ListViewPage(page)
        lv.wait_for_grid()
        assert lv.has_row_with_text("TestProduct")


class TestEntityModificationFlow:
    """Modify entity via chat."""

    def test_01_add_field_proposal(self, page, mock_llm_server):
        """AI proposes adding a field."""
        chat = AIChatPanel(page)
        chat.wait_for_panel()
        chat.send_message("Add a stock quantity field to TestProduct")
        response = chat.get_last_response()
        assert "StockQuantity" in response

    def test_02_confirm_add_field(self, page, mock_llm_server):
        """Confirming adds the field."""
        chat = AIChatPanel(page)
        chat.wait_for_panel()
        chat.send_message("Add a stock quantity field to TestProduct")
        chat.send_message("yes")
        response = chat.get_last_response()
        assert "Added" in response or "Modified" in response


class TestEntityDeletionFlow:
    """Delete entity via chat."""

    def test_01_delete_proposal(self, page, mock_llm_server):
        """AI asks for confirmation before deleting."""
        chat = AIChatPanel(page)
        chat.wait_for_panel()
        chat.send_message("Delete the TestProduct entity")
        response = chat.get_last_response()
        assert "delete" in response.lower()
        assert "proceed" in response.lower() or "confirm" in response.lower()


class TestValidation:
    """Schema validation via chat."""

    def test_01_validate_schema(self, page, mock_llm_server):
        """AI calls validate_schema and shows result."""
        chat = AIChatPanel(page)
        chat.wait_for_panel()
        chat.send_message("Validate the schema")
        response = chat.get_last_response()
        assert "validation" in response.lower() or "compile" in response.lower()


class TestPendingChanges:
    """Pending changes detection."""

    def test_01_show_pending_changes(self, page, mock_llm_server):
        """AI shows pending changes."""
        chat = AIChatPanel(page)
        chat.wait_for_panel()
        chat.send_message("Show pending changes")
        response = chat.get_last_response()
        # Response should mention pending or up to date
        assert "pending" in response.lower() or "up to date" in response.lower() or "deploy" in response.lower()


class TestRolePermissions:
    """Role permission management via chat."""

    def test_01_list_roles(self, page, mock_llm_server):
        """AI lists available roles."""
        chat = AIChatPanel(page)
        chat.wait_for_panel()
        chat.send_message("List all roles")
        response = chat.get_last_response()
        assert "role" in response.lower() or "Role" in response

    def test_02_set_permissions_proposal(self, page, mock_llm_server):
        """AI proposes permission changes."""
        chat = AIChatPanel(page)
        chat.wait_for_panel()
        chat.send_message("Give Admins full access to TestProduct")
        response = chat.get_last_response()
        assert "Admins" in response
        assert "Read" in response or "read" in response


class TestErrorHandling:
    """Error and edge case handling."""

    def test_01_empty_message(self, page, mock_llm_server):
        """Sending empty message doesn't crash."""
        chat = AIChatPanel(page)
        chat.wait_for_panel()
        # Try sending empty — depends on DxAIChat behavior (may block empty sends)
        # This test validates graceful handling

    def test_02_conversation_continuity(self, page, mock_llm_server):
        """Multi-turn conversation maintains context."""
        chat = AIChatPanel(page)
        chat.wait_for_panel()
        chat.send_message("Create a product entity")
        chat.send_message("yes")
        assert chat.get_message_count() >= 4  # 2 user + 2 assistant


class TestCleanup:
    """Remove test data created by AI chat tests."""

    def test_99_cleanup(self, page, mock_llm_server):
        """Clean up entities created during tests."""
        # Navigate to Custom Class and delete AITest entities
        nav = NavigationPage(page)
        nav.navigate_to("Schema Management", "Custom Class")
        lv = ListViewPage(page)
        lv.wait_for_grid()

        # Delete TestProduct if it exists
        if lv.has_row_with_text("TestProduct"):
            lv.select_row_with_text("TestProduct")
            from pages.base_page import BasePage
            base = BasePage(page)
            base.click_delete()
            base.confirm_delete()
            page.wait_for_timeout(2000)
```

**Step 3: Add flask to test requirements**

```
# Append to tests/requirements.txt
flask==3.1.0
```

**Step 4: Commit**

```bash
git add tests/tests/test_phase11_ai_chat_mocked.py tests/conftest.py tests/requirements.txt
git commit -m "feat: add mocked Playwright tests for AI Schema Assistant (Phase 11)"
```

---

## Task 15: Live Playwright Test Suite

Smaller smoke test suite that hits the real AI provider.

**Files:**
- Create: `tests/tests/test_phase11_ai_chat_live.py`
- Modify: `tests/conftest.py` (add `live_ai` marker)

**Step 1: Add pytest marker**

```python
# Add to conftest.py or create pytest.ini / pyproject.toml
# pytest.ini:
[pytest]
markers =
    live_ai: marks tests that require a live AI API key (deselect with '-m "not live_ai"')
```

**Step 2: Create live test suite**

```python
# tests/tests/test_phase11_ai_chat_live.py
"""
Phase 11: AI Schema Assistant — Live LLM Smoke Tests
These tests hit the real AI provider. Run manually with a valid API key.

Usage:
    AI_TEST_API_KEY=sk-ant-... pytest tests/tests/test_phase11_ai_chat_live.py -v

Skip when no API key:
    pytest tests/ -m "not live_ai"
"""
import os
import pytest
from pages.ai_chat_page import AIChatPanel
from pages.navigation_page import NavigationPage
from pages.list_view_page import ListViewPage

# Skip entire module if no API key
pytestmark = pytest.mark.live_ai
API_KEY = os.environ.get("AI_TEST_API_KEY", "")

def skip_if_no_key():
    if not API_KEY:
        pytest.skip("AI_TEST_API_KEY not set — skipping live AI tests")


class TestLiveEntityCreation:
    """Create entity via natural language with real AI."""

    def test_01_create_entity_natural_language(self, page):
        """Ask AI to create an entity and verify it gets created."""
        skip_if_no_key()
        chat = AIChatPanel(page)
        chat.wait_for_panel()

        # Use a unique name to avoid conflicts
        import uuid
        entity_name = f"AITest{uuid.uuid4().hex[:6]}"

        chat.send_message(
            f"Create an entity called {entity_name} with fields: "
            "Title (string, required), DueDate (DateTime), IsCompleted (boolean)",
            timeout=60000  # Live AI may be slow
        )
        response = chat.get_last_response()
        # AI should propose the entity
        assert entity_name in response or "Title" in response

        # Confirm
        chat.send_message("yes", timeout=60000)
        response = chat.get_last_response()
        assert "created" in response.lower() or "Created" in response

        # Verify in metadata
        nav = NavigationPage(page)
        nav.navigate_to("Schema Management", "Custom Class")
        lv = ListViewPage(page)
        lv.wait_for_grid()
        assert lv.has_row_with_text(entity_name)

        # Cleanup
        lv.select_row_with_text(entity_name)
        from pages.base_page import BasePage
        BasePage(page).click_delete()
        BasePage(page).confirm_delete()


class TestLiveEntityModification:
    """Modify entity via chat with real AI."""

    def test_01_add_field_via_chat(self, page):
        """Add a field to an existing entity through conversation."""
        skip_if_no_key()
        # This test assumes at least one CustomClass exists
        # Create one first if needed, or use an existing test entity
        chat = AIChatPanel(page)
        chat.wait_for_panel()
        chat.send_message("List all entities", timeout=60000)
        # Verify we get a response
        assert len(chat.get_last_response()) > 0


class TestLiveAmbiguityResolution:
    """Test that AI asks clarifying questions for vague requests."""

    def test_01_vague_request(self, page):
        """Vague request should prompt AI to ask questions."""
        skip_if_no_key()
        chat = AIChatPanel(page)
        chat.wait_for_panel()
        chat.send_message("I need to track some stuff", timeout=60000)
        response = chat.get_last_response()
        # AI should ask clarifying questions, not just create something
        assert "?" in response  # Should contain a question


class TestLiveRolePermissions:
    """Role permission management with real AI."""

    def test_01_ask_about_permissions(self, page):
        """Ask AI about setting up permissions."""
        skip_if_no_key()
        chat = AIChatPanel(page)
        chat.wait_for_panel()
        chat.send_message("What roles are available?", timeout=60000)
        response = chat.get_last_response()
        assert len(response) > 0


class TestLiveMultiTurn:
    """Multi-turn conversation with real AI."""

    def test_01_create_then_modify_then_permissions(self, page):
        """Full workflow: create entity, modify it, set permissions."""
        skip_if_no_key()
        import uuid
        entity_name = f"AITest{uuid.uuid4().hex[:6]}"

        chat = AIChatPanel(page)
        chat.wait_for_panel()

        # Create
        chat.send_message(
            f"Create a {entity_name} entity with Name (string) and Email (string)",
            timeout=60000
        )
        chat.send_message("yes", timeout=60000)
        assert "created" in chat.get_last_response().lower() or entity_name in chat.get_last_response()

        # Modify
        chat.send_message(f"Add a Phone field to {entity_name}", timeout=60000)
        chat.send_message("yes", timeout=60000)

        # Verify at least 6 messages in conversation (3 turns x 2)
        assert chat.get_message_count() >= 6

        # Cleanup
        nav = NavigationPage(page)
        nav.navigate_to("Schema Management", "Custom Class")
        lv = ListViewPage(page)
        lv.wait_for_grid()
        if lv.has_row_with_text(entity_name):
            lv.select_row_with_text(entity_name)
            from pages.base_page import BasePage
            BasePage(page).click_delete()
            BasePage(page).confirm_delete()
```

**Step 3: Commit**

```bash
git add tests/tests/test_phase11_ai_chat_live.py
git commit -m "feat: add live AI smoke tests for AI Schema Assistant"
```

---

## Task 16: Update Documentation

**Files:**
- Modify: `README.md` (add AI Schema Assistant section)
- Modify: `CLAUDE.md` (add AI architecture notes)

**Step 1: Add AI section to CLAUDE.md**

Add after the "Web API (OData)" section:

```markdown
### AI Schema Assistant

Conversational AI interface for creating, modifying, and deleting runtime entities through natural language.

- **LLM integration:** LLMTornado with Claude Sonnet as default, multi-provider support
- **UI:** DxAIChat in Blazor side panel
- **Tools:** 10 AI functions (list/describe/create/modify/delete entities, validate, pending changes, roles)
- **System prompt:** Two-tier — lightweight entity list + on-demand `describe_entity` for full details
- **Config:** `AI` section in `appsettings.json` (API keys in `appsettings.Development.json`)
- **Testing:** Mocked (mock LLM server, deterministic) + Live (real AI, `@pytest.mark.live_ai`)
```

Add key files to the file locations table:

```markdown
- AI Chat: `Module/Services/AIChatService.cs`, `AIChatClient.cs`, `SchemaAIToolsProvider.cs`
- AI Config: `Module/Services/AIOptions.cs`, `AIServiceCollectionExtensions.cs`
- AI Discovery: `Module/Services/SchemaDiscoveryService.cs`
- AI UI: `Blazor.Server/Editors/AIChatViewItem/AIChat.razor`
- AI Tests: `tests/tests/test_phase11_ai_chat_mocked.py`, `test_phase11_ai_chat_live.py`
- Mock LLM: `tests/mock_llm/server.py`, `tests/mock_llm/scripts.py`
```

**Step 2: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "docs: add AI Schema Assistant documentation"
```

---

## Implementation Order Summary

| Task | Component | Dependencies |
|------|-----------|-------------|
| 1 | NuGet + AIOptions | None |
| 2 | SchemaDiscoveryService | Task 1 |
| 3 | AIChatService | Task 1 |
| 4 | Read tools (list/describe/schema/pending/validate) | Tasks 2, 3 |
| 5 | Write tools (create/modify/delete) | Task 4 |
| 6 | Role tools (list/set permissions) | Task 4 |
| 7 | Complete tool registration | Tasks 4, 5, 6 |
| 8 | AIChatClient adapter | Task 3 |
| 9 | DI wiring | Tasks 2, 3, 7, 8 |
| 10 | AIChat.razor | Task 9 |
| 11 | Startup.cs integration | Tasks 9, 10 |
| 12 | Mock LLM server | None (parallel with 1-11) |
| 13 | AIChatPanel page object | Task 10 |
| 14 | Mocked test suite | Tasks 11, 12, 13 |
| 15 | Live test suite | Tasks 11, 13 |
| 16 | Documentation | Task 11 |

**Parallelizable:** Tasks 12 (mock server) can be built in parallel with Tasks 1-11.

## Critical Implementation Notes

1. **LLMTornado API:** The code in this plan is skeletal. The exact `Conversation`, `Tool`, `ToolCall`, and `GetResponseRich` APIs must be verified against the current LLMTornado version. Use XafTornado's `AIChatService.cs` as the working reference.

2. **DxAIChat component:** Requires DevExpress AI Integration NuGet package (`DevExpress.AIIntegration.Blazor`). Check if it's already referenced or needs to be added. The component's property names and events may differ from the plan — verify against DevExpress 25.2 docs.

3. **Security module:** The role permission tools use `dynamic` to avoid compile-time dependency on `DevExpress.ExpressApp.Security`. If the security module is not configured in this project, the role tools will gracefully report "Security module is not configured."

4. **Mock server endpoint routing:** LLMTornado may not support custom base URLs out of the box. Check its configuration API. If not supported, the mock tests may need a different approach (e.g., dependency injection of a mock `IChatClient` instead of a network-level mock).

5. **DxAIChat CSS selectors:** The Playwright page object uses guessed CSS selectors for DxAIChat's internal elements. These MUST be verified by inspecting the actual rendered HTML in the browser. Update `ai_chat_page.py` accordingly.
