# AI Schema Assistant — Design Document

## Overview

Add a conversational AI interface to XafDynamicAssemblies that lets users create, modify, and delete runtime entity schemas through natural language. The AI operates on `CustomClass` and `CustomField` metadata, confirms changes before applying, and suggests deployment when there are pending changes. It also manages XAF role permissions for runtime entities.

## Target Users

Both business users ("I need to track equipment maintenance with serial numbers and service dates") and developers ("create MaintenanceRecord with SerialNumber string, LastServiceDate DateTime"). The AI adapts its hand-holding based on how specific the request is.

## Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Scope | Full schema lifecycle + roles | Create, modify, delete entities/fields, manage permissions. No data CRUD (future phase). |
| Confirmation | Smart defaults, confirm before saving | AI infers types (e.g., "price" → Decimal), presents summary, waits for approval |
| UI | Side panel (Blazor) | Persistent chat alongside Schema Management views for context |
| Deploy trigger | AI suggests, user clicks | AI says "You have pending changes" but never triggers restart (exit code 42) |
| Schema awareness | Metadata + ITypesInfo | Query CustomClass/CustomField for pending changes, ITypesInfo for live schema |
| LLM integration | LLMTornado, Claude default | Multi-provider (Anthropic, OpenAI, Google, Mistral, etc.). GitHub Copilot not yet supported by LLMTornado. |
| Reuse from XafTornado | Build fresh, informed by patterns | XafDynamicAssemblies becomes the primary product; XafTornado capabilities migrate here over time |
| Markdown rendering | Markdig + HtmlSanitizer (server-side) | Blazor Server stack; duetGPT's React approach doesn't apply |

## AI Tools

### Schema Management

| Tool | Purpose |
|------|---------|
| `list_entities` | List all CustomClasses with status (Runtime/Graduating/Compiled), field count, deployed state |
| `describe_entity` | Show all fields, types, relationships, and properties for a CustomClass |
| `get_active_schema` | ITypesInfo reflection — compiled + deployed runtime types currently live |
| `create_entity` | Create a CustomClass + CustomFields. Returns summary for user confirmation before persisting. |
| `modify_entity` | Add/remove/rename fields, change types, update properties on an existing CustomClass |
| `delete_entity` | Remove a CustomClass and its fields (with confirmation) |
| `validate_schema` | Run Roslyn test-compile to check for errors without deploying |
| `get_pending_changes` | Compare metadata vs. live schema — show what would change on deploy |

### Role Permissions

| Tool | Purpose |
|------|---------|
| `list_roles` | Show existing PermissionPolicyRoles and their type permissions |
| `set_role_permissions` | Add/modify type permissions on a role for an entity (Read/Write/Create/Delete). Confirms before saving. |

## Confirmation Flow

```
User: "I need to track equipment maintenance — serial number, last service date, technician"

AI: I'll create MaintenanceRecord with these fields:
    - SerialNumber (String)
    - LastServiceDate (DateTime)
    - Technician (Reference -> Employee)
    Look good?

User: "yes"

AI: Created MaintenanceRecord with 3 fields.
    You have pending changes — click Deploy when ready.

User: "give Technicians read and write access to it"

AI: I'll update the Technicians role:
    - MaintenanceRecord: Read, Write, Create (enabled)
    - MaintenanceRecord: Delete (disabled)
    Look good?

User: "yes, and also allow delete"

AI: Updated Technicians role — full CRUD access to MaintenanceRecord.
```

## Architecture

### Services

| Service | Responsibility |
|---------|---------------|
| `AIChatService` | LLMTornado integration, conversation history, tool-calling loop, Polly retry (3 attempts, exponential backoff) |
| `AIChatClient` | IChatClient adapter for DevExpress DxAIChat control |
| `SchemaAIToolsProvider` | 10 tools above, operating on CustomClass/CustomField/PermissionPolicyRole via ObjectSpace |
| `SchemaDiscoveryService` | ITypesInfo reflection for live schema awareness |
| `AIOptions` | Config model: Model, DefaultProvider, ApiKeys, MaxOutputTokens, MaxToolIterations, TimeoutSeconds |
| `ServiceCollectionExtensions` | DI registration (`AddAIServices`) |

### System Prompt Strategy

Two-tier, optimized for token efficiency:

1. **System prompt** (always included): lists existing CustomClasses (name, status, field count), supported field types, compiled entities available for references, and available roles
2. **On-demand** (via tools): `describe_entity` and `get_active_schema` provide full details only when the AI needs them

### UI

- **DxAIChat** component in a Blazor side panel
- Visible alongside Schema Management navigation group
- Markdown rendering via Markdig + HtmlSanitizer for formatted responses (tables, code blocks)
- Prompt suggestions: "Create a new entity", "Add a field to...", "Show pending changes", "Set up permissions"

### Configuration

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

API keys stored in `appsettings.Development.json` (gitignored).

## File Structure (Planned)

```
XafDynamicAssemblies.Module/
  Services/
    AIChatService.cs           # LLMTornado + tool loop + Polly
    AIChatClient.cs            # IChatClient adapter
    SchemaAIToolsProvider.cs   # 10 AI tools
    SchemaDiscoveryService.cs  # ITypesInfo reflection
    AIOptions.cs               # Config model
    ServiceCollectionExtensions.cs  # AddAIServices()

XafDynamicAssemblies.Blazor.Server/
  Editors/
    AIChatViewItem/
      AIChat.razor             # DxAIChat side panel component
  Startup.cs                   # Wire up AddAIServices()
```

## Testing Strategy

### Approach: Mocked (CI) + Live (Smoke)

Two test suites, both Playwright Python, following the existing project pattern (page objects in `tests/pages/`).

### Mocked Suite (CI / Regression)

A mock LLM endpoint replaces the real AI provider. The server is configured to route AI requests to a local mock that returns deterministic responses. This tests:

| Area | Tests |
|------|-------|
| Chat UI | Panel opens/closes, message rendering, prompt suggestions, markdown formatting |
| Entity creation flow | User sends message -> AI proposes entity -> user confirms -> CustomClass + CustomFields created |
| Entity modification flow | Add field, remove field, rename field, change type via chat |
| Entity deletion flow | AI confirms before deleting, metadata removed |
| Validation | AI calls test-compile, error messages displayed in chat |
| Pending changes | AI detects undeployed changes, suggests deploy |
| Role permissions | List roles, set permissions via chat, confirmation flow |
| Error handling | LLM timeout, invalid tool arguments, network failures |
| Conversation context | Multi-turn conversations maintain state correctly |

**Mock implementation:** A lightweight ASP.NET Core endpoint (or Python Flask) that mimics the LLMTornado API contract, returning pre-scripted tool calls and responses based on input patterns. Configured via `appsettings.Testing.json` pointing to `http://localhost:{mock_port}`.

### Live Suite (Manual Smoke)

A smaller set of end-to-end tests that hit the real AI provider. Run manually (not in CI) to verify the full pipeline. Marked with `@pytest.mark.live_ai` so they can be selected/excluded.

| Test | Purpose |
|------|---------|
| Create entity via natural language | "I need a Product with name, price, and description" -> verify entity created |
| Modify entity via chat | "Add a stock quantity field to Product" -> verify field added |
| Ambiguity resolution | Vague request -> AI asks clarifying questions -> user answers -> correct result |
| Role permission via chat | "Give Admins full access to Product" -> verify permissions set |
| Multi-turn conversation | Create entity, then modify it, then set permissions — all in one conversation |

**Requirements:** Valid API key in environment variable `AI_TEST_API_KEY`. Tests skip gracefully if key is not set.

### Test Infrastructure

- Page object: `AIChatPanel` — wraps DxAIChat component interactions (send message, wait for response, read messages, click suggestions)
- Mock server: starts/stops as a pytest fixture, configurable response scripts
- Test data cleanup: each test creates entities with unique prefixed names (e.g., `AITest_Product_<uuid>`) and cleans up after

## Out of Scope (Future Phases)

- Data CRUD on runtime entities (port XafTornado's query/create/update tools)
- Navigation/filtering of views via AI
- WinForms support
- Deploy trigger from AI
- GitHub Copilot as LLM provider (pending LLMTornado support)
- Export/import of role definitions

## Dependencies (New NuGet Packages)

- `LlmTornado` — multi-provider LLM client
- `Microsoft.Extensions.Resilience` (Polly) — retry pipeline
- `Markdig` — Markdown to HTML
- `HtmlSanitizer` — prevent XSS in rendered HTML
