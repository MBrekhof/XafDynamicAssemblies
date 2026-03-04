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

- **.NET 8** / C#, DevExpress XAF 25.2, EF Core 8
- **Roslyn** (`Microsoft.CodeAnalysis.CSharp` 4.10) for runtime compilation
- **PostgreSQL 17** via Docker: `localhost:5434`, db `XafDynamicAssemblies`, user/pass `xafdynamic`
- **EF Core provider:** `Npgsql.EntityFrameworkCore.PostgreSQL` 8.0.11
- **Blazor Server** (primary UI) + WinForms (secondary)
- **Docker:** `docker compose up -d` starts PostgreSQL + Python utility container

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

XafDynamicAssemblies.Win/             # WinForms host (Windows-only, net8.0-windows)
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

### Entity Relationships

Runtime entities can reference compiled entities (e.g., runtime `EmployeeInformation` → compiled `Company`) and other runtime entities (all compiled in same Roslyn unit). Real SQL FK constraints are created. Inverse navigation on compiled entities is not supported.

### Web API (OData)

Runtime entities can be exposed as OData REST endpoints via XAF's built-in Web API module. Set `IsApiExposed = true` on a CustomClass, then Deploy — after restart, full CRUD endpoints are live at `/api/odata/{ClassName}`.

- **Registration:** `services.AddXafWebApi()` in Startup.cs registers `CustomClass`, `CustomField`, and any runtime types with `IsApiExposed = true`
- **Timing:** `EarlyBootstrap()` compiles runtime types before XAF init so they're available for Web API endpoint registration in `ConfigureServices`
- **OData features:** $filter, $select, $expand, $orderby, $top, $skip, $count
- **Swagger:** Available at `/swagger` in development mode
- **Endpoint refresh:** Process restart (exit code 42) re-registers endpoints based on current metadata

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
