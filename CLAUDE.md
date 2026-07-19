# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

AI-powered dynamic assemblies system for DevExpress XAF. Enables runtime entity creation — new business object types, properties, and relationships defined at runtime without recompilation. Uses Roslyn for in-process C# compilation and collectible `AssemblyLoadContext` for hot-loading.

**Not EAV** — generates real CLR types with real SQL columns and FK constraints.

## Build & Run

```bash
# Solution file (new .slnx format)
dotnet build XafDynamicAssemblies.slnx

# Run the Blazor Server app
dotnet run --project XafDynamicAssemblies/XafDynamicAssemblies.Blazor.Server

# Update database via CLI
dotnet run --project XafDynamicAssemblies/XafDynamicAssemblies.Blazor.Server -- --updateDatabase

# Build configurations: Debug, Release, EasyTest
dotnet build XafDynamicAssemblies.slnx -c EasyTest
```

## Tech Stack

- **.NET 10** / C#, DevExpress XAF 26.1, EF Core 10
- **Roslyn** (`Microsoft.CodeAnalysis.CSharp` 5.0) for runtime compilation
- **PostgreSQL 17** via Docker: `localhost:5434`, db `XafDynamicAssemblies`, user/pass `xafdynamic`
- **EF Core provider:** `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3
- **Blazor Server** (UI)
- **Docker:** `docker compose up -d` starts PostgreSQL

## Architecture

### Solution Structure

```
XafDynamicAssemblies.Module/          # Shared module — all business logic lives here
  BusinessObjects/                    # EF Core DbContext + entity classes
  DatabaseUpdate/                     # XAF database updater
  Module.cs                           # XafDynamicAssembliesModule — registers XAF sub-modules

XafDynamicAssemblies.Blazor.Server/   # Blazor Server host
  Startup.cs                          # DI, XAF builder, EF Core provider config
  BlazorApplication.cs                # XAF BlazorApplication with DB version mismatch handling

```

### Core Pattern: Dynamic Entity System

Two metadata tables drive everything:

- `CustomClass` (ClassName, NavigationGroup, Description, Status, IsApiExposed)
- `CustomField` (CustomClassId, FieldName, TypeName, IsDefaultField, Description)

**Startup sequence:** Query metadata → Roslyn compiles all runtime classes into one assembly → `AssemblyLoadContext` loads it → TypesInfo registers types → EF Core model rebuilt → XAF views auto-generated.

**Hot-load sequence (no restart):** `SchemaSynchronizer` runs DDL → Roslyn recompiles → drain active UoW → unload old ALC → load new ALC → rebuild EF Core IModel → refresh TypesInfo → SignalR push to clients.

### Key Implementation Classes

| Class | Responsibility |
|---|---|
| `RuntimeAssemblyBuilder` | Generates C# source per CustomClass, Roslyn-compiles into one assembly |
| `AssemblyGenerationManager` | Manages versioned collectible ALCs, drain/unload/load lifecycle |
| `DynamicModelCacheKeyFactory` | Forces EF Core model rebuild via ModelVersion counter |
| `SchemaSynchronizer` | Executes DDL (ALTER TABLE) against PostgreSQL before assembly rebuild |
| `SchemaChangeOrchestrator` | Coordinates hot-load: DDL → compile → restart via exit code 42 |
| `GraduationService` | Generates production C# source + DbContext snippet for graduating entities |
| `AIChatService` | LLMTornado integration, conversation history, tool loop, Polly retry |
| `SchemaAIToolsProvider` | 10 AI tools for schema CRUD and role management |
| `SchemaDiscoveryService` | ITypesInfo reflection for AI system prompt |
| `MetadataActionDispatcherController` | Materializes CustomAction rows as SimpleActions on DetailView via a constructor-declared 10-slot pool; live, no restart |
| `StepValueConverter` | Converts a CustomActionStep string literal to its target member type (SetField), invariant culture |

### Entity Relationships

Runtime entities can reference compiled entities (e.g., runtime `EmployeeInformation` → compiled `Company`) and other runtime entities (all compiled in same Roslyn unit). Real SQL FK constraints are created. Inverse navigation on compiled entities is not supported.

### Web API (OData)

Runtime entities can be exposed as OData REST endpoints via XAF's built-in Web API module. Set `IsApiExposed = true` on a CustomClass, then Deploy — after restart, full CRUD endpoints are live at `/api/odata/{ClassName}`.

- **Registration:** `services.AddXafWebApi()` in Startup.cs registers `CustomClass`, `CustomField`, and any runtime types with `IsApiExposed = true`
- **Timing:** `EarlyBootstrap()` compiles runtime types before XAF init so they're available for Web API endpoint registration in `ConfigureServices`
- **OData features:** $filter, $select, $expand, $orderby, $top, $skip, $count
- **Swagger:** Available at `/swagger` in development mode
- **Endpoint refresh:** Process restart (exit code 42) re-registers endpoints based on current metadata

### Metadata Actions

`CustomAction`/`CustomActionStep` let admins add DetailView buttons (SetField, ShowMessage, OpenView steps) as pure metadata — no Roslyn, no compilation, no restart. `MetadataActionDispatcherController` (`ViewController<DetailView>`) declares a fixed pool of 10 slot `SimpleAction`s in its **constructor** — XAF Blazor resolves Ribbon container membership from constructor-declared actions only, so actions created in `OnActivated` never render. On each `OnActivated` it queries active `CustomAction` rows matching `View.ObjectTypeInfo.Name`, assigns them to slots in deterministic order (Caption, then ID), and hides unused slots — a **10-actions-per-entity ceiling**; overflow is logged, not thrown. A new/changed action appears the next time its DetailView opens — genuinely live, unlike the deploy/restart cycle used for schema changes. Steps run in `SortOrder`: SetField resolves the member via `ObjectTypeInfo.FindMember` and converts the literal through `StepValueConverter`; ShowMessage displays immediately; OpenView resolves the target type by **simple name** (runtime types first, then compiled `XafTypesInfo`, falling back to `FindTypeInfo` for fully-qualified names — same resolution precedent as `SchemaAIToolsProvider`) and opens its ListView after the loop. One `ObjectSpace.CommitChanges()` fires only if at least one SetField ran and no step aborted. Metadata validation (XAF Validation module, on save): required fields per step kind, unique (TargetEntity, Caption), at most one OpenView step per action, and criteria parseability as a save-time warning (target type may not exist yet).

### AI Schema Assistant

Conversational AI interface for creating, modifying, and deleting runtime entities through natural language.

- **LLM integration:** LLMTornado with Claude Sonnet as default, multi-provider support
- **UI:** DxAIChat as navigation item (Schema Management group)
- **Tools:** 10 AI functions (list/describe/create/modify/delete entities, validate, pending changes, roles)
- **System prompt:** Two-tier — lightweight entity list + on-demand `describe_entity` for full details
- **Config:** `AI` section in `appsettings.json` (API keys in `appsettings.Development.json`)
- **Testing:** Mocked (in-process mock LLM server on port 5555, deterministic) + Live (real AI, `[Trait("Category","LiveAI")]`, opt-in via `AI_TEST_API_KEY`)

### Graduation Path

Runtime entities can be "graduated" to compiled code: `Status = Runtime → Graduating → Compiled`. The generated C# source, DbContext snippet, and migration note are exported. The compiled class takes over the existing SQL table with zero data migration.

## XAF Conventions

- Business objects derive from `BaseObject` (EF Core path)
- DbContext: `XafDynamicAssembliesEFCoreDbContext` with deferred deletion, optimistic locking, `ChangingAndChangedNotificationsWithOriginalValues`
- Module registration pattern: `RequiredModuleTypes.Add(typeof(...))` in Module constructor
- Database auto-updates when debugger is attached; throws version mismatch error in production
- Connection string key: `ConnectionString` in `appsettings.json`

## Type Mapping (SchemaSynchronizer — PostgreSQL)

```
System.String   → text
System.Int32    → integer
System.Int64    → bigint
System.Decimal  → numeric(18,6)
System.Double   → double precision
System.Single   → real
System.Boolean  → boolean
System.DateTime → timestamp without time zone
System.Guid     → uuid
System.Byte[]   → bytea
```

## File Locations

- Entities: `Module/BusinessObjects/CustomClass.cs`, `CustomField.cs`
- DbContext: `Module/BusinessObjects/XafDynamicAssembliesDbContext.cs`
- Runtime assembly: `Module/Services/RuntimeAssemblyBuilder.cs`, `AssemblyGenerationManager.cs`
- Hot-load: `Module/Services/SchemaChangeOrchestrator.cs`, `Module/Controllers/SchemaChangeController.cs`
- Model cache: `Module/Services/DynamicModelCacheKeyFactory.cs`
- Graduation: `Module/Services/GraduationService.cs`, `Module/Controllers/GraduateController.cs`
- Restart: `Blazor.Server/Services/RestartService.cs`, `Blazor.Server/Program.cs` (exit code 42)
- SignalR: `Blazor.Server/Hubs/SchemaUpdateHub.cs`, `Blazor.Server/Pages/_Host.cshtml`
- AI Chat: `Module/Services/AIChatService.cs`, `AIChatClient.cs`, `SchemaAIToolsProvider.cs`
- AI Config: `Module/Services/AIOptions.cs`, `AIServiceCollectionExtensions.cs`
- AI Discovery: `Module/Services/SchemaDiscoveryService.cs`
- AI UI: `Blazor.Server/Editors/AIChatViewItem/AIChat.razor`
- AI Tests: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/Phase11_AIChatMockedTests.cs`, `Phase11_AIChatLiveTests.cs`
- Mock LLM: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/MockLlm/MockLlmServer.cs`, `ScriptMatcher.cs`
- Metadata Actions: `Module/BusinessObjects/CustomAction.cs`, `CustomActionStep.cs`
- Metadata Actions dispatcher: `Module/Controllers/MetadataActionDispatcherController.cs`
- Metadata Actions converter: `Module/Services/StepValueConverter.cs`
- Metadata Actions tests: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/Phase12_ActionBuilderTests.cs`, `StepValueConverterTests.cs`
- Tests: `XafDynamicAssemblies/XafDynamicAssemblies.Tests` (Playwright .NET/xUnit, page objects in `Pages/`)
