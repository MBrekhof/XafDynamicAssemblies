# Project Brief: XafDynamicAssemblies + Elsa Workflows + Hangfire

## What We're Building

An extension of the existing **XafDynamicAssemblies** project
(https://github.com/MBrekhof/XafDynamicAssemblies) that adds **no-code workflow
automation** on top of the existing no-code entity creation system.

The goal: a user with zero developer involvement can:
1. Define entities (e.g. Customer, Order, Invoice) via the existing AI Chat / Schema UI
2. Define a workflow trigger rule: "when Order.Finished = true → create an Invoice"
3. Have that workflow execute automatically, with no code written

---

## Existing Foundation (Do Not Break)

The XafDynamicAssemblies repo already provides:
- **Runtime entity creation** via Roslyn compilation — define entities, fields, and
  relationships through a UI or AI Chat, click Deploy, server restarts, entities appear
- **AI Schema Assistant** (AIChatService + SchemaAIToolsProvider) — natural language
  entity CRUD using LLMTornado + Claude Sonnet
- **XAF Web API (OData)** — runtime entities exposed as REST endpoints at
  `/api/odata/{ClassName}` after deploy
- **Exit-code-42 restart mechanism** — deploy triggers process restart, ANCM/Docker
  relaunches automatically
- **docker-compose.yml** — PostgreSQL + Python test runner already containerized

All new work must coexist with this without breaking it.

---

## Architecture: Three Docker Containers

```
┌─────────────────────┐   HTTP    ┌─────────────────────┐
│  Container 1        │──────────▶│  Container 2         │
│  XAF Blazor Server  │           │  Elsa Workflows      │
│  + Hangfire         │◀──────────│  (standalone API)    │
│                     │  OData    │                      │
└──────────┬──────────┘           └──────────┬───────────┘
           │                                  │
           └──────────────┬───────────────────┘
                          ▼
               ┌─────────────────────┐
               │  Container 3         │
               │  PostgreSQL 17       │
               │  (shared DB)         │
               └─────────────────────┘
```

**Key architectural decision**: XAF and Elsa run in **separate containers**.
This eliminates all DI/DbContext conflicts. They communicate exclusively via HTTP:
- Hangfire → Elsa: dispatch workflow via Elsa REST API
- Elsa → XAF: create/update entities via XAF's existing OData API

---

## New Components to Build

### 1. `WorkflowTriggerRule` — Compiled Entity (in XafDynamicAssemblies.Module)

A new persistent entity stored alongside CustomClass/CustomField:

```csharp
[DefaultClassOptions]
[NavigationItem("Workflow")]
public class WorkflowTriggerRule : BaseObject
{
    public string EntityTypeName { get; set; }   // e.g. "Order"
    public string WatchField { get; set; }        // e.g. "Finished"
    public string WatchValue { get; set; }        // e.g. "true"
    public string WorkflowDefinitionId { get; set; } // Elsa workflow ID
    public bool IsActive { get; set; } = true;
}
```

This is a plain compiled XAF entity — no Roslyn needed. Users define trigger rules
through the normal XAF CRUD UI.

### 2. `DynamicWorkflowTriggerJob` — Hangfire Job (in XafDynamicAssemblies.Blazor.Server)

A recurring Hangfire job that:
1. Loads all active `WorkflowTriggerRule` records
2. For each rule, executes a raw SQL query against the dynamic entity's table
3. Finds rows where `WatchField = WatchValue` AND `WorkflowTriggered = false`
4. POSTs to Elsa's dispatch endpoint for each matching row
5. Marks matched rows as `WorkflowTriggered = true` (idempotency)

```csharp
public class DynamicWorkflowTriggerJob
{
    private readonly IDbContextFactory<XafDynamicAssembliesDbContext> _dbFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rules = await db.WorkflowTriggerRules
            .Where(r => r.IsActive)
            .ToListAsync(ct);

        var elsa = _httpFactory.CreateClient("Elsa");

        foreach (var rule in rules)
        {
            // Raw SQL — dynamic entity tables are not in the DbContext at compile time
            var sql = $"""
                SELECT "Id" FROM "{rule.EntityTypeName}"
                WHERE "{rule.WatchField}" = @watchValue
                AND "WorkflowTriggered" = false
                """;

            var ids = await db.Database
                .SqlQueryRaw<Guid>(sql, new NpgsqlParameter("watchValue", rule.WatchValue))
                .ToListAsync(ct);

            foreach (var id in ids)
            {
                await elsa.PostAsJsonAsync("/api/workflow-instances/dispatch", new
                {
                    WorkflowDefinitionId = rule.WorkflowDefinitionId,
                    Input = new Dictionary<string, object>
                    {
                        ["EntityType"] = rule.EntityTypeName,
                        ["EntityId"] = id,
                        ["TriggerRule"] = rule.Id
                    }
                }, ct);

                // Mark as triggered (idempotency)
                await db.Database.ExecuteSqlRawAsync(
                    $"""UPDATE "{rule.EntityTypeName}" SET "WorkflowTriggered" = true WHERE "Id" = @id""",
                    new NpgsqlParameter("id", id), ct);
            }
        }
    }
}
```

**Important**: the `WorkflowTriggered` bool field must be added to the dynamic entity
by convention. When a `WorkflowTriggerRule` targets an entity, the system should warn
the user (or auto-add) that a `WorkflowTriggered` field is needed on that entity.
Consider adding this automatically when a trigger rule is saved.

### 3. Elsa Container — Standalone Workflow API

A **new, separate .NET project** (`XafDynamicAssemblies.Elsa`) that:
- Is a minimal ASP.NET Core app hosting Elsa 3.x
- Has its own EF Core context for Elsa persistence (separate schema in the shared PG DB)
- Exposes Elsa's built-in REST API for workflow dispatch
- Contains a small library of **generic, schema-aware activities**

#### Generic Activities (the core of the no-code value):

```csharp
// Creates any runtime entity by name, mapping fields from input
[Activity("XafDynamic", "Create Entity")]
public class CreateDynamicEntityActivity : CodeActivity
{
    [Input(Description = "XAF OData base URL")]
    public Input<string> XafBaseUrl { get; set; }

    [Input(Description = "Entity type name to create, e.g. Invoice")]
    public Input<string> TargetEntityType { get; set; }

    [Input(Description = "Field values as key-value pairs")]
    public Input<Dictionary<string, object>> FieldValues { get; set; }

    [Output]
    public Output<string> CreatedEntityId { get; set; }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext ctx)
    {
        var http = ctx.GetRequiredService<IHttpClientFactory>().CreateClient();
        var baseUrl = ctx.Get(XafBaseUrl);
        var entityType = ctx.Get(TargetEntityType);
        var fields = ctx.Get(FieldValues);

        var response = await http.PostAsJsonAsync(
            $"{baseUrl}/api/odata/{entityType}", fields);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        ctx.Set(CreatedEntityId, result.GetProperty("Id").GetString());
    }
}

// Reads a field value from an existing runtime entity
[Activity("XafDynamic", "Get Entity Field")]
public class GetDynamicEntityFieldActivity : CodeActivity
{
    [Input] public Input<string> XafBaseUrl { get; set; }
    [Input] public Input<string> EntityType { get; set; }
    [Input] public Input<string> EntityId { get; set; }
    [Input] public Input<string> FieldName { get; set; }
    [Output] public Output<object> FieldValue { get; set; }
    // ... implementation via OData GET
}

// Updates a field on an existing runtime entity
[Activity("XafDynamic", "Update Entity Field")]
public class UpdateDynamicEntityFieldActivity : CodeActivity
{
    // ... PATCH via OData
}
```

#### Workflow Definition — Stored as JSON, registered at startup:

The "Order.Finished → create Invoice" workflow is a JSON definition referencing the
generic activities above. It can be:
- Hand-authored JSON (developer path)
- Generated by the AI Chat extension (no-code path — see section 5)

### 4. docker-compose.yml Extensions

Add to the existing docker-compose.yml:

```yaml
services:
  # existing: postgres, python-tests

  xaf:
    build:
      context: .
      dockerfile: XafDynamicAssemblies/Dockerfile
    environment:
      - ConnectionStrings__DefaultConnection=Host=postgres;Port=5432;Database=XafDynamicAssemblies;Username=xafdynamic;Password=xafdynamic
      - Elsa__BaseUrl=http://elsa:5002
      - Hangfire__PollIntervalSeconds=60
    ports:
      - "5001:5001"
    depends_on:
      - postgres
      - elsa
    restart: unless-stopped  # handles exit code 42

  elsa:
    build:
      context: .
      dockerfile: XafDynamicAssemblies.Elsa/Dockerfile
    environment:
      - ConnectionStrings__DefaultConnection=Host=postgres;Port=5432;Database=XafDynamicAssemblies;Username=xafdynamic;Password=xafdynamic
      - Xaf__ODataBaseUrl=http://xaf:5001
    ports:
      - "5002:5002"
    depends_on:
      - postgres
    restart: unless-stopped
```

### 5. AI Chat Extension (Optional but High Value)

Extend `AIChatService` and `SchemaAIToolsProvider` with additional tools:

```
New AI tools:
- create_workflow_trigger_rule(entityType, watchField, watchValue, workflowId)
- list_workflow_trigger_rules()
- delete_workflow_trigger_rule(id)
- generate_workflow_definition(description) → returns Elsa JSON, POSTs to Elsa API
```

This enables the full no-code experience in one conversation:

> *"Create an Order entity with Customer (reference), Amount (decimal), Finished
> (bool). When Finished becomes true, create an Invoice with the same Customer
> and Amount."*

The AI creates the schema AND the trigger rule AND the Elsa workflow definition.

---

## Data Flow (End-to-End)

```
1. User defines Order + Invoice entities via AI Chat or Schema UI
2. User defines WorkflowTriggerRule:
     EntityTypeName = "Order"
     WatchField     = "Finished"
     WatchValue     = "true"
     WorkflowDefinitionId = "generate-invoice-v1"
3. User sets Order.Finished = true in the XAF UI
4. Hangfire job fires (every N seconds, configurable):
     - Queries: SELECT Id FROM "Order" WHERE "Finished" = true AND "WorkflowTriggered" = false
     - Finds the order
     - POSTs to Elsa: dispatch workflow "generate-invoice-v1" with OrderId
     - Sets Order.WorkflowTriggered = true
5. Elsa executes "generate-invoice-v1":
     - GetDynamicEntityField: GET /api/odata/Order(orderId) → reads Customer, Amount
     - CreateDynamicEntity: POST /api/odata/Invoice { Customer, Amount, OrderId }
6. Invoice appears in XAF UI
```

---

## Project Structure Addition

```
XafDynamicAssemblies/               ← existing, unchanged
XafDynamicAssemblies.Module/        ← add WorkflowTriggerRule entity here
XafDynamicAssemblies.Blazor.Server/ ← add Hangfire job + registration here
XafDynamicAssemblies.Elsa/          ← NEW project
  ├── Program.cs                    ← Elsa + EF Core + ASP.NET Core setup
  ├── Activities/
  │   ├── CreateDynamicEntityActivity.cs
  │   ├── GetDynamicEntityFieldActivity.cs
  │   └── UpdateDynamicEntityFieldActivity.cs
  ├── Dockerfile
  └── appsettings.json
```

---

## Key Constraints and Decisions

1. **No shared DI between XAF and Elsa** — they are separate processes/containers.
   All communication is HTTP only.

2. **Elsa uses its own EF schema** in the shared PostgreSQL database (different table
   prefix, e.g. `elsa_*`). No conflicts with XAF tables.

3. **Idempotency is critical** — the `WorkflowTriggered` bool on dynamic entities
   prevents double-firing. The Hangfire job must set this atomically after dispatch.
   Consider using a DB transaction spanning the dispatch + flag update, or accepting
   at-least-once delivery with Elsa workflow deduplication.

4. **WorkflowTriggered field convention** — when saving a WorkflowTriggerRule, the
   system should verify (or auto-create) a `WorkflowTriggered` bool field on the
   target entity. This can be done in a XAF Controller's `Saved` event on
   WorkflowTriggerRule.

5. **Elsa 3.x** is the target version (not Elsa 2.x — breaking API differences).
   Use `Elsa.Api.Client` NuGet package for the dispatch call from Hangfire if available,
   otherwise plain HttpClient is fine.

6. **Hangfire poll interval** should be configurable via appsettings, defaulting to
   60 seconds. Expose it in the XAF UI via a simple settings entity if needed.

7. **Authentication between containers** — for the PoC, use a shared API key passed
   as a header. Both containers read it from environment variables.

---

## What Success Looks Like (PoC Acceptance Criteria)

- [ ] Start `docker compose up` — three containers start cleanly
- [ ] Navigate to XAF, define Order + Invoice entities via AI Chat
- [ ] Define a WorkflowTriggerRule via XAF UI (no code)
- [ ] Create an Order record, set Finished = true
- [ ] Within 60 seconds, an Invoice record appears automatically
- [ ] Hangfire dashboard (`/hangfire`) shows job execution history
- [ ] Elsa dashboard shows workflow execution history
- [ ] No developer intervention required after initial docker compose up

---

## Out of Scope for PoC

- Elsa visual workflow designer embedded in XAF UI (nice to have, post-PoC)
- Complex multi-step workflows (approval chains, timers, human tasks)
- Error handling / retry UI (Elsa provides this out of the box in its own dashboard)
- Graduating workflow definitions to compiled code (analogous to entity graduation)
