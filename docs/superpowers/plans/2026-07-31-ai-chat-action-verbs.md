# AI-Chat Action Verbs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the AI schema assistant four tools to manage metadata actions (live DetailView buttons): `list_actions`, `create_action`, `delete_action`, `set_action_active`.

**Architecture:** Extend `SchemaAIToolsProvider` (10 → 14 tools) following its existing method-per-tool pattern; add one capability paragraph to the system prompt; extend the mock LLM's `ScriptMatcher` with an action block; add 3 Phase 11 E2E tests that verify real DB effects and real button rendering (not just canned chat text).

**Tech Stack:** .NET 10, DevExpress XAF 26.1.3 (Blazor), EF Core 10, Microsoft.Extensions.AI `AIFunctionFactory`, xUnit + Microsoft.Playwright, in-process mock LLM.

**Spec:** `docs/superpowers/specs/2026-07-31-ai-chat-action-verbs-design.md`

## Global Constraints

- No new NuGet packages. No changes to `MetadataActionDispatcherController`, entities, or validation rules.
- Tool methods NEVER throw — catch-all `return $"Error: …: {ex.Message}";` (existing provider pattern).
- **Mock tool_use input keys MUST equal the C# parameter names exactly** (`caption`, `targetEntity`, `criteria`, `confirmationMessage`, `stepsJson`, `entityName`, `isActive`). Known trap: the existing mock sends `class_name` to `create_entity` which expects `className`, so that tool silently errors and tests pass on canned follow-up text only. Do not replicate this.
- The chat UI displays only the LLM's (mock's) canned follow-up text, never raw tool results — E2E assertions about tool effects go to the DATABASE or the rendered UI, not chat text.
- E2E tests (Task 4 only) need: docker postgres up (`docker compose up -d`, port 5434) and the app started via `run-server-mock.bat` (sets `AI_MOCK_LLM_BASE_URL=http://localhost:5555`). Check port 5001 free before starting; kill the server after (see Task 4).
- Phase 11 E2E assumes the `Customer` runtime entity exists and is deployed (created by Phase02 — same precedent as the documented Phase04 dependency). Full-suite order satisfies it.

---

### Task 1: Four action tools in SchemaAIToolsProvider

**Files:**
- Modify: `XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/SchemaAIToolsProvider.cs`

**Interfaces:**
- Consumes: `CustomAction` / `CustomActionStep` / `StepKind` / `StepMessageType` (existing, `XafDynamicAssemblies.Module.BusinessObjects`), `CreateObjectSpace()` helper, `XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes`, `XafTypesInfo.Instance.PersistentTypes` (all already used in this file).
- Produces: tools `list_actions(entityName)`, `create_action(caption, targetEntity, criteria, confirmationMessage, stepsJson)`, `delete_action(caption, targetEntity)`, `set_action_active(caption, targetEntity, isActive)` — registered in `CreateTools()`. Task 3's mock and Task 4's tests rely on these exact names and parameter names.

- [ ] **Step 1: Register the four tools in `CreateTools()`**

In `CreateTools()`, after the two Role tools entries, add:

```csharp
            // Metadata action tools (live DetailView buttons — ACT-001)
            AIFunctionFactory.Create(ListActions, "list_actions"),
            AIFunctionFactory.Create(CreateAction, "create_action"),
            AIFunctionFactory.Create(DeleteAction, "delete_action"),
            AIFunctionFactory.Create(SetActionActive, "set_action_active"),
```

- [ ] **Step 2: Add the four tool methods and the DTO**

Add a new section before the `// JSON DTOs for tool parameters` section:

```csharp
    // ==========================================================================
    // METADATA ACTION TOOLS (live DetailView buttons — ACT-001)
    // ==========================================================================

    [Description("List metadata actions (live DetailView buttons): caption, target entity, active state, steps, criteria. Returns a markdown table.")]
    private string ListActions(
        [Description("Only list actions for this target entity. Optional — pass empty to list all.")] string entityName)
    {
        _logger.LogInformation("[Tool:list_actions] Called with entity={Entity}", entityName);
        try
        {
            using var scope = CreateObjectSpace();
            var query = scope.Os.GetObjectsQuery<CustomAction>();
            if (!string.IsNullOrWhiteSpace(entityName))
                query = query.Where(a => a.TargetEntity == entityName);
            var actions = query.OrderBy(a => a.TargetEntity).ThenBy(a => a.Caption).ToList();

            if (actions.Count == 0)
                return string.IsNullOrWhiteSpace(entityName)
                    ? "No metadata actions defined yet. Use `create_action` to add a live button to an entity's detail view."
                    : $"No metadata actions defined for '{entityName}'. Use `create_action` to add one.";

            var sb = new StringBuilder();
            sb.AppendLine("| Caption | Target Entity | Active | Steps | Criteria |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var a in actions)
            {
                var steps = string.Join("; ", (a.Steps ?? Enumerable.Empty<CustomActionStep>().ToList())
                    .OrderBy(s => s.SortOrder).Select(s => s.DisplayText));
                sb.AppendLine($"| {a.Caption} | {a.TargetEntity} | {(a.IsActive ? "Yes" : "No")} | {steps} | {a.Criteria} |");
            }
            sb.AppendLine();
            sb.AppendLine($"Total: {actions.Count} action(s)");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Tool:list_actions] Error");
            return $"Error listing actions: {ex.Message}";
        }
    }

    [Description("Create a metadata action — a button on an entity's DetailView that runs steps (SetField, ShowMessage, OpenView). LIVE the next time the detail view opens: no deploy, no restart.")]
    private string CreateAction(
        [Description("Button caption (e.g. 'Approve'). Must be unique per target entity.")] string caption,
        [Description("Entity whose DetailView gets the button (simple name, e.g. 'Order'). May be a runtime or compiled entity.")] string targetEntity,
        [Description("XAF criteria string enabling the button conditionally (e.g. \"Status != 'Approved'\"). Optional.")] string criteria,
        [Description("Confirmation prompt shown before executing. Optional.")] string confirmationMessage,
        [Description("JSON array of steps, executed in array order. Each: {\"kind\": \"SetField\"|\"ShowMessage\"|\"OpenView\", \"fieldName\": \"...\", \"value\": \"...\", \"messageText\": \"...\", \"messageType\": \"Info\"|\"Success\"|\"Warning\"|\"Error\", \"targetEntityName\": \"...\"}. SetField needs fieldName (+value); ShowMessage needs messageText; OpenView needs targetEntityName. At most one OpenView step.")] string stepsJson)
    {
        _logger.LogInformation("[Tool:create_action] Creating {Caption} on {Entity}", caption, targetEntity);
        try
        {
            if (string.IsNullOrWhiteSpace(caption))
                return "Error: caption is required.";
            if (string.IsNullOrWhiteSpace(targetEntity))
                return "Error: targetEntity is required.";

            using var scope = CreateObjectSpace();
            var duplicate = scope.Os.GetObjectsQuery<CustomAction>()
                .FirstOrDefault(a => a.Caption == caption && a.TargetEntity == targetEntity);
            if (duplicate != null)
                return $"Error: An action '{caption}' already exists on '{targetEntity}'. Use delete_action first, then recreate it.";

            List<StepDefinition> stepDefs;
            try
            {
                stepDefs = JsonSerializer.Deserialize<List<StepDefinition>>(stepsJson ?? "",
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException jex)
            {
                return $"Error: stepsJson is not valid JSON: {jex.Message}";
            }
            if (stepDefs == null || stepDefs.Count == 0)
                return "Error: stepsJson must contain at least one step — a button with no steps does nothing.";

            // Mirror the XAF save rules — they do not fire on this non-secured ObjectSpace.
            var parsed = new List<(StepKind Kind, StepDefinition Def)>();
            foreach (var sd in stepDefs)
            {
                if (!Enum.TryParse<StepKind>(sd.Kind, ignoreCase: true, out var kind))
                    return $"Error: Unknown step kind '{sd.Kind}'. Valid kinds: SetField, ShowMessage, OpenView.";
                if (kind == StepKind.SetField && string.IsNullOrWhiteSpace(sd.FieldName))
                    return "Error: A SetField step requires fieldName.";
                if (kind == StepKind.ShowMessage && string.IsNullOrWhiteSpace(sd.MessageText))
                    return "Error: A ShowMessage step requires messageText.";
                if (kind == StepKind.OpenView && string.IsNullOrWhiteSpace(sd.TargetEntityName))
                    return "Error: An OpenView step requires targetEntityName.";
                parsed.Add((kind, sd));
            }
            if (parsed.Count(p => p.Kind == StepKind.OpenView) > 1)
                return "Error: An action may contain at most one OpenView step.";

            var warnings = new List<string>();
            if (!string.IsNullOrWhiteSpace(criteria))
            {
                try { DevExpress.Data.Filtering.CriteriaOperator.Parse(criteria); }
                catch { warnings.Add($"Criteria \"{criteria}\" could not be parsed — the button will be disabled until it is fixed."); }
            }

            var targetTypeExists =
                XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes.Any(t => t.Name == targetEntity)
                || XafTypesInfo.Instance.PersistentTypes.Any(ti => ti.Name == targetEntity);
            if (!targetTypeExists)
                warnings.Add($"Entity '{targetEntity}' is not currently a live runtime or compiled type — the button appears once that entity exists (created + deployed).");

            var activeCount = scope.Os.GetObjectsQuery<CustomAction>()
                .Count(a => a.TargetEntity == targetEntity && a.IsActive);
            if (activeCount >= 10)
                warnings.Add($"'{targetEntity}' already has {activeCount} active actions — the dispatcher renders at most 10 per entity, so this one may not appear until others are removed or deactivated.");

            var action = scope.Os.CreateObject<CustomAction>();
            action.Caption = caption;
            action.TargetEntity = targetEntity;
            action.Criteria = string.IsNullOrWhiteSpace(criteria) ? null : criteria;
            action.ConfirmationMessage = string.IsNullOrWhiteSpace(confirmationMessage) ? null : confirmationMessage;
            action.IsActive = true;

            for (var i = 0; i < parsed.Count; i++)
            {
                var (kind, sd) = parsed[i];
                var step = scope.Os.CreateObject<CustomActionStep>();
                step.CustomAction = action;
                step.SortOrder = i;
                step.Kind = kind;
                step.FieldName = sd.FieldName;
                step.Value = sd.Value;
                step.MessageText = sd.MessageText;
                step.MessageType = Enum.TryParse<StepMessageType>(sd.MessageType, ignoreCase: true, out var mt)
                    ? mt : StepMessageType.Info;
                step.TargetEntityName = sd.TargetEntityName;
                action.Steps.Add(step);
            }

            scope.Os.CommitChanges();

            var sb = new StringBuilder();
            sb.AppendLine($"Action '{caption}' created on '{targetEntity}' with {parsed.Count} step(s).");
            sb.AppendLine("It is LIVE the next time that entity's detail view opens — no deploy, no restart needed.");
            foreach (var w in warnings)
                sb.AppendLine($"- Warning: {w}");
            _logger.LogInformation("[Tool:create_action] Created {Caption} on {Entity} with {Steps} steps",
                caption, targetEntity, parsed.Count);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Tool:create_action] Error");
            return $"Error creating action: {ex.Message}";
        }
    }

    [Description("Delete a metadata action (and its steps) identified by caption + target entity. The button disappears the next time the detail view opens.")]
    private string DeleteAction(
        [Description("The action's caption (e.g. 'Approve').")] string caption,
        [Description("The entity the action targets (e.g. 'Order').")] string targetEntity)
    {
        _logger.LogInformation("[Tool:delete_action] Deleting {Caption} on {Entity}", caption, targetEntity);
        try
        {
            if (string.IsNullOrWhiteSpace(caption) || string.IsNullOrWhiteSpace(targetEntity))
                return "Error: caption and targetEntity are required.";

            using var scope = CreateObjectSpace();
            var action = scope.Os.GetObjectsQuery<CustomAction>()
                .FirstOrDefault(a => a.Caption == caption && a.TargetEntity == targetEntity);
            if (action == null)
            {
                var available = string.Join(", ", scope.Os.GetObjectsQuery<CustomAction>()
                    .Where(a => a.TargetEntity == targetEntity)
                    .Select(a => a.Caption).OrderBy(c => c));
                return $"Action '{caption}' on '{targetEntity}' not found. Actions on that entity: {(string.IsNullOrEmpty(available) ? "none" : available)}";
            }

            var stepCount = action.Steps?.Count ?? 0;
            if (action.Steps != null)
            {
                foreach (var step in action.Steps.ToList())
                    scope.Os.Delete(step);
            }
            scope.Os.Delete(action);
            scope.Os.CommitChanges();

            _logger.LogInformation("[Tool:delete_action] Deleted {Caption} on {Entity}", caption, targetEntity);
            return $"Action '{caption}' on '{targetEntity}' deleted ({stepCount} step(s) removed). The button disappears the next time the detail view opens.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Tool:delete_action] Error");
            return $"Error deleting action: {ex.Message}";
        }
    }

    [Description("Enable or disable a metadata action without deleting it. Takes effect the next time the entity's detail view opens.")]
    private string SetActionActive(
        [Description("The action's caption (e.g. 'Approve').")] string caption,
        [Description("The entity the action targets (e.g. 'Order').")] string targetEntity,
        [Description("true to enable the button, false to hide it.")] bool isActive)
    {
        _logger.LogInformation("[Tool:set_action_active] {Caption} on {Entity} -> {Active}", caption, targetEntity, isActive);
        try
        {
            if (string.IsNullOrWhiteSpace(caption) || string.IsNullOrWhiteSpace(targetEntity))
                return "Error: caption and targetEntity are required.";

            using var scope = CreateObjectSpace();
            var action = scope.Os.GetObjectsQuery<CustomAction>()
                .FirstOrDefault(a => a.Caption == caption && a.TargetEntity == targetEntity);
            if (action == null)
            {
                var available = string.Join(", ", scope.Os.GetObjectsQuery<CustomAction>()
                    .Where(a => a.TargetEntity == targetEntity)
                    .Select(a => a.Caption).OrderBy(c => c));
                return $"Action '{caption}' on '{targetEntity}' not found. Actions on that entity: {(string.IsNullOrEmpty(available) ? "none" : available)}";
            }

            action.IsActive = isActive;
            scope.Os.CommitChanges();
            return $"Action '{caption}' on '{targetEntity}' is now {(isActive ? "active" : "inactive")}. Takes effect the next time the detail view opens.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Tool:set_action_active] Error");
            return $"Error setting action active state: {ex.Message}";
        }
    }
```

And add this DTO next to `FieldDefinition` / `ModificationsPayload` at the bottom of the class:

```csharp
    private sealed class StepDefinition
    {
        public string Kind { get; set; }
        public string FieldName { get; set; }
        public string Value { get; set; }
        public string MessageText { get; set; }
        public string MessageType { get; set; }
        public string TargetEntityName { get; set; }
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build XafDynamicAssemblies.slnx`
Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/SchemaAIToolsProvider.cs
git commit -m "feat: 4 AI action tools — list/create/delete/set_active for metadata actions"
```

---

### Task 2: System prompt capability paragraph

**Files:**
- Modify: `XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/SchemaDiscoveryService.cs` (inside `GenerateSystemPrompt`)

**Interfaces:**
- Consumes: nothing new.
- Produces: prompt text only — no code contract.

- [ ] **Step 1: Add the paragraph**

In `GenerateSystemPrompt`, directly after the `## Rules` block's closing `sb.AppendLine();` and before the `## Supported Field Types` section, insert:

```csharp
            // Metadata actions capability (AI-chat action verbs)
            sb.AppendLine("## Metadata Actions (Live Buttons)");
            sb.AppendLine("You can add buttons to an entity's detail view with `create_action` (steps: SetField, ShowMessage, OpenView).");
            sb.AppendLine("Actions are pure metadata: they appear the NEXT time the detail view opens — no Deploy, no restart.");
            sb.AppendLine("Inspect with `list_actions`, remove with `delete_action`, enable/disable with `set_action_active`.");
            sb.AppendLine("At most 10 active actions per entity render (dispatcher slot ceiling).");
            sb.AppendLine();
```

- [ ] **Step 2: Build**

Run: `dotnet build XafDynamicAssemblies.slnx`
Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/SchemaDiscoveryService.cs
git commit -m "feat: system prompt paragraph for metadata action tools"
```

---

### Task 3: Mock LLM matchers + self-tests (TDD)

**Files:**
- Modify: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/MockLlm/ScriptMatcher.cs`
- Test: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/MockLlmServerTests.cs`

**Interfaces:**
- Consumes: `ScriptMatcher.Match/MatchToolResult/ToolUse/Text/ExtractEntityName` (existing), tool names + parameter names from Task 1.
- Produces: canned tool_use payloads Task 4's E2E relies on — `create_action` with caption `"Approve"`, targetEntity from the LAST quoted word, and a fixed 2-step stepsJson (SetField Status=Approved + ShowMessage).

- [ ] **Step 1: Write the failing self-tests**

Append to `MockLlmServerTests`:

```csharp
    [Fact]
    public async Task Anthropic_AddButton_Returns_ToolUse_CreateAction()
    {
        var body = new
        {
            model = "mock-model",
            messages = new object[] { new { role = "user", content = "add an 'Approve' button to 'Customer'" } },
        };
        var resp = await _client.PostAsJsonAsync("/v1/messages", body);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tool_use", json.GetProperty("stop_reason").GetString());
        var block = json.GetProperty("content")[0];
        Assert.Equal("create_action", block.GetProperty("name").GetString());

        var input = block.GetProperty("input");
        // Keys must equal the real tool's C# parameter names (see Global Constraints).
        Assert.Equal("Approve", input.GetProperty("caption").GetString());
        Assert.Equal("Customer", input.GetProperty("targetEntity").GetString());
        var steps = JsonSerializer.Deserialize<JsonElement>(input.GetProperty("stepsJson").GetString()!);
        Assert.Equal(2, steps.GetArrayLength());
        Assert.Equal("SetField", steps[0].GetProperty("kind").GetString());
        Assert.Equal("ShowMessage", steps[1].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Anthropic_DisableAction_Returns_SetActionActive_False()
    {
        var body = new
        {
            model = "mock-model",
            messages = new object[] { new { role = "user", content = "disable the 'Approve' action on 'Customer'" } },
        };
        var resp = await _client.PostAsJsonAsync("/v1/messages", body);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var block = json.GetProperty("content")[0];
        Assert.Equal("set_action_active", block.GetProperty("name").GetString());
        var input = block.GetProperty("input");
        Assert.Equal("Approve", input.GetProperty("caption").GetString());
        Assert.Equal("Customer", input.GetProperty("targetEntity").GetString());
        Assert.False(input.GetProperty("isActive").GetBoolean());
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests --filter "FullyQualifiedName~MockLlmServerTests"`
Expected: the 2 new tests FAIL (matcher falls through to the delete rule / default text); the 5 existing ones PASS.

- [ ] **Step 3: Implement the matcher block**

In `ScriptMatcher.Match`, directly after the `if (IsConfirmation(lower)) return BuildConfirm();` line, add:

```csharp
        // Metadata action verbs. MUST run before the generic delete/create matchers below,
        // which would otherwise shadow "delete the Approve action" / "create an action".
        if (lower.Contains("action") || lower.Contains("button"))
        {
            if (lower.Contains("list") || lower.Contains("what"))
                return ToolUse("list_actions", new Dictionary<string, object> { ["entityName"] = "" });
            if (lower.Contains("disable") || lower.Contains("deactivate") || lower.Contains("enable") || lower.Contains("activate"))
                return ToolUse("set_action_active", new Dictionary<string, object>
                {
                    ["caption"] = "Approve",
                    ["targetEntity"] = ExtractEntityName(userMessage),
                    // NOTE: "deactivate".Contains("activate") is true — decide by the negative words.
                    ["isActive"] = !(lower.Contains("disable") || lower.Contains("deactivate")),
                });
            if (lower.Contains("delete") || lower.Contains("remove"))
                return ToolUse("delete_action", new Dictionary<string, object>
                {
                    ["caption"] = "Approve",
                    ["targetEntity"] = ExtractEntityName(userMessage),
                });
            if (lower.Contains("add") || lower.Contains("create"))
                return ToolUse("create_action", new Dictionary<string, object>
                {
                    ["caption"] = "Approve",
                    ["targetEntity"] = ExtractEntityName(userMessage),
                    ["criteria"] = "",
                    ["confirmationMessage"] = "",
                    ["stepsJson"] = "[{\"kind\":\"SetField\",\"fieldName\":\"Status\",\"value\":\"Approved\"},{\"kind\":\"ShowMessage\",\"messageText\":\"Approved via chat\",\"messageType\":\"Success\"}]",
                });
            // No verb matched — fall through to the generic rules.
        }
```

And in `ToolResultFollowups`, add:

```csharp
        ["list_actions"] = "Here are the metadata actions.",
        ["create_action"] = "Action created — the button appears the next time the entity's detail view opens. No deploy needed.",
        ["delete_action"] = "Action deleted.",
        ["set_action_active"] = "Action active state updated.",
```

- [ ] **Step 4: Run to verify all pass**

Run: `dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests --filter "FullyQualifiedName~MockLlmServerTests"`
Expected: 7/7 PASS.

- [ ] **Step 5: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Tests/MockLlm/ScriptMatcher.cs XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/MockLlmServerTests.cs
git commit -m "test: mock LLM matchers + self-tests for action verbs"
```

---

### Task 4: Phase 11 E2E tests (Test_16–18)

**Files:**
- Modify: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/Phase11_AIChatMockedTests.cs`

**Interfaces:**
- Consumes: `AIChatPanel` page object, `ResetMockStateAsync()` (existing in this file), `ListViewPage`, `DatabaseHelper.GetConnection()` (`XafDynamicAssemblies.Tests.Helpers`), Task 3's canned payloads, Task 1's tools. Tables: `"CustomActions"` / `"CustomActionSteps"` (PK column `"ID"`).
- Produces: nothing downstream.

- [ ] **Step 1: Add the three tests**

The file currently ends at `Test_15`. Add `using Npgsql;` and `using XafDynamicAssemblies.Tests.Helpers;` if not present, then append (tests run alphabetically — `Test_16` → `Test_18`):

```csharp
    // --- Action verbs (AI-chat action verbs, 2026-07-31 spec) ---
    //
    // The chat displays only the mock's canned follow-up text, never raw tool results —
    // so effect assertions go to the DB (real rows) and to the rendered DetailView button.
    // Cross-suite dependency: 'Customer' must exist and be deployed (Phase02 creates it —
    // same precedent as Phase04's documented dependency).

    private static long CountActionsInDb(string caption, string targetEntity)
    {
        using var conn = DatabaseHelper.GetConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM \"CustomActions\" WHERE \"Caption\" = @c AND \"TargetEntity\" = @t", conn);
        cmd.Parameters.AddWithValue("c", caption);
        cmd.Parameters.AddWithValue("t", targetEntity);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Create an action via chat; verify the REAL rows (not just canned chat text).</summary>
    [Fact]
    public async Task Test_16_CreateActionViaChat()
    {
        await ResetMockStateAsync();
        var chat = new AIChatPanel(_page);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        await chat.SendMessageAsync("add an 'Approve' button to 'Customer'", 30_000);
        var response = await chat.GetLastResponseAsync();
        Assert.True(response.Length > 0, "Should receive a follow-up response");

        Assert.Equal(1L, CountActionsInDb("Approve", "Customer"));
        using var conn = DatabaseHelper.GetConnection();
        using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*) FROM ""CustomActionSteps"" s
            JOIN ""CustomActions"" a ON s.""CustomActionId"" = a.""ID""
            WHERE a.""Caption"" = 'Approve' AND a.""TargetEntity"" = 'Customer'", conn);
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
    }

    /// <summary>The ACT-001 integration seam: the chat-created button really renders — no restart.</summary>
    [Fact]
    public async Task Test_17_ChatCreatedButtonRenders()
    {
        await _page.GotoAsync($"{TestSettings.BaseUrl}/Customer_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(2000);
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(1500);

        // data-action-name carries the action CAPTION in DX 26.1 (memory: RibbonItemModelMapper).
        var btn = _page.Locator(
            "dxbl-toolbar-item > button[data-action-name=\"Approve\"], dxbl-bar-item > button[data-action-name=\"Approve\"]");
        Assert.True(await btn.CountAsync() > 0,
            "Chat-created 'Approve' button should render on the Customer DetailView without a restart");
    }

    /// <summary>Disable via chat (DB IsActive=false), then delete via chat (rows gone). Doubles as cleanup.</summary>
    [Fact]
    public async Task Test_18_ToggleAndDeleteActionViaChat()
    {
        await ResetMockStateAsync();
        var chat = new AIChatPanel(_page);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        await chat.SendMessageAsync("disable the 'Approve' action on 'Customer'", 30_000);
        using (var conn = DatabaseHelper.GetConnection())
        using (var cmd = new NpgsqlCommand(
            "SELECT \"IsActive\" FROM \"CustomActions\" WHERE \"Caption\" = 'Approve' AND \"TargetEntity\" = 'Customer'", conn))
        {
            Assert.False((bool)cmd.ExecuteScalar()!, "Action should be inactive after 'disable' via chat");
        }

        await chat.SendMessageAsync("delete the 'Approve' action on 'Customer'", 30_000);
        Assert.Equal(0L, CountActionsInDb("Approve", "Customer"));
        using (var conn = DatabaseHelper.GetConnection())
        using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM \"CustomActionSteps\"", conn))
        {
            // Steps are aggregated — deleting the action removes its steps. Other phases don't
            // create CustomActionSteps rows before Phase 12, so zero here is exact.
            Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        }
    }
```

- [ ] **Step 2: Build**

Run: `dotnet build XafDynamicAssemblies.slnx`
Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Start services**

```bash
docker compose up -d                      # postgres on 5434
netstat -ano | grep :5001                 # must be free (kill stale: taskkill //PID <pid> //F //T)
```
Start the app in a separate terminal/background: `run-server-mock.bat` (from the repo root; it must keep running). Wait until `curl -sk -o /dev/null -w "%{http_code}" https://localhost:5001` prints 200.

- [ ] **Step 4: Run Phase 11 — full phase, not just the new tests**

Run: `dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests --filter "FullyQualifiedName~Phase11&Category!=LiveAI"`
Expected: 18/18 PASS. Running the whole phase also proves the new matcher block didn't shadow any existing script (no old test regresses).
Note: Test_16–18 need `Customer` deployed — if running Phase 11 standalone on a fresh DB, run Phase02 first (same as the documented Phase04 dependency).

- [ ] **Step 5: Stop services**

Kill the `run-server-mock.bat` process tree (`netstat -ano | grep :5001` → `taskkill //PID <pid> //F //T`); leave postgres as you found it.

- [ ] **Step 6: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/Phase11_AIChatMockedTests.cs
git commit -m "test: Phase11 E2E for action verbs — DB effects + live button render"
```

---

### Task 5: Documentation counts

**Files:**
- Modify: `CLAUDE.md` (repo root)
- Modify: `README.md` (repo root)

**Interfaces:** none — docs only.

- [ ] **Step 1: CLAUDE.md**

Two exact replacements:
- `| \`SchemaAIToolsProvider\` | 10 AI tools for schema CRUD and role management |` → `| \`SchemaAIToolsProvider\` | 14 AI tools for schema CRUD, role management, and metadata actions |`
- `- **Tools:** 10 AI functions (list/describe/create/modify/delete entities, validate, pending changes, roles)` → `- **Tools:** 14 AI functions (list/describe/create/modify/delete entities, validate, pending changes, roles, and metadata actions: list/create/delete/toggle)`

- [ ] **Step 2: README.md**

Grep README.md for `10 AI` and `10 tools`; update each hit the same way: the count becomes 14 and the enumeration gains "metadata actions (list_actions, create_action, delete_action, set_action_active)". If the AI Schema Assistant section lists tools individually, append the four new ones with one-line descriptions matching the `[Description]` texts from Task 1.

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md README.md
git commit -m "docs: AI tool count 10 -> 14 (metadata action verbs)"
```
