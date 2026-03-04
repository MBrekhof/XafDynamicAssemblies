# XafDynamicAssemblies

AI-powered dynamic entity system for [DevExpress XAF](https://www.devexpress.com/products/net/application_framework/). Create new business object types, properties, and relationships **at runtime** — no recompilation, no redeployment. Uses Roslyn for in-process C# compilation and `AssemblyLoadContext` for hot-loading.

**This is not EAV.** The system generates real CLR types backed by real SQL columns and foreign key constraints. The result is full XAF framework support — list views, detail views, validation, reporting — for entities that never existed at compile time.

## What It Does

Define a new entity through the UI:

1. **Create a class** — give it a name, navigation group, and description
2. **Add fields** — string, int, decimal, bool, DateTime, Guid, or references to other entities
3. **Click Deploy** — Roslyn compiles the class, DDL creates the table, the server restarts, and your new entity appears in the navigation with full CRUD views

The entire cycle takes seconds. No developer intervention required.

## Key Features

- **Runtime entity creation** via metadata-driven Roslyn compilation
- **Hot-load deploy** with automatic process restart and SignalR client reconnection
- **Entity relationships** — runtime entities can reference other runtime entities or compiled entities, with real SQL foreign keys
- **Graduation path** — promote runtime entities to compiled C# source code for inclusion in the main codebase
- **Degraded mode** — if compilation fails at startup, compiled entities still work normally
- **Error recovery** — fix bad metadata, redeploy, and the system recovers without manual intervention
- **Full validation** — class names, field names, type names, and reserved words are validated before save
- **68 end-to-end tests** across 8 phases, all passing

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Runtime | .NET 8, C# 12 |
| Framework | DevExpress XAF 25.2, EF Core 8 |
| Compilation | Roslyn (Microsoft.CodeAnalysis.CSharp 4.10) |
| Database | PostgreSQL 17 (via Npgsql) |
| UI | Blazor Server (primary), WinForms (secondary) |
| Real-time | SignalR for schema change notifications |
| Testing | Playwright (Python) + pytest, 68 E2E tests |
| Infrastructure | Docker Compose (PostgreSQL + test runner) |

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (for PostgreSQL and test runner)
- [DevExpress Universal Subscription](https://www.devexpress.com/) (XAF 25.2) — NuGet feed must be configured

## Quick Start

```bash
# 1. Start PostgreSQL and the Python test container
docker compose up -d

# 2. Build the solution
dotnet build XafDynamicAssemblies.slnx

# 3. Initialize the database
dotnet run --project XafDynamicAssemblies/XafDynamicAssemblies.Blazor.Server \
  -- --updateDatabase --forceUpdate --silent

# 4. Run the server with auto-restart support
./run-server.bat          # Windows
./run-server.sh           # Linux/macOS
```

Open https://localhost:5001 in your browser.

## Usage

### Creating a Runtime Entity

1. Navigate to **Schema Management > Custom Class**
2. Click **New**, enter a class name (e.g. `Invoice`), navigation group (e.g. `Billing`)
3. Save, then navigate to **Schema Management > Custom Field**
4. Add fields — each needs a name, type, and parent class
5. Return to **Custom Class** and click **Deploy Schema**
6. The server restarts — your entity appears in the nav with full CRUD

### Supported Field Types

| Type | C# Type | PostgreSQL |
|------|---------|-----------|
| String | `string` | `text` |
| Integer | `int?` | `integer` |
| Long | `long?` | `bigint` |
| Decimal | `decimal?` | `numeric(18,6)` |
| Double | `double?` | `double precision` |
| Float | `float?` | `real` |
| Boolean | `bool?` | `boolean` |
| DateTime | `DateTime?` | `timestamp` |
| Guid | `Guid?` | `uuid` |
| Reference | Navigation + FK | `uuid` (FK constraint) |

### Entity Relationships

Runtime entities can reference:
- **Other runtime entities** — compiled in the same Roslyn assembly
- **Compiled entities** — e.g., a runtime `EmployeeInfo` referencing the compiled `Company` entity

All references create real SQL foreign key constraints.

### Graduating to Compiled Code

When a runtime entity is stable:

1. Open it in **Custom Class** detail view
2. Click **Graduate**
3. The system generates production C# source, a DbContext snippet, and a migration note
4. Copy the code into your project and deploy
5. The graduated entity takes over the existing SQL table — zero data migration

## Solution Structure

```
XafDynamicAssemblies/
├── XafDynamicAssemblies.Module/          # Shared module — all business logic
│   ├── BusinessObjects/                  # EF Core entities + DbContext
│   │   ├── CustomClass.cs               # Runtime entity metadata
│   │   ├── CustomField.cs               # Runtime field definitions
│   │   └── XafDynamicAssembliesDbContext.cs
│   ├── Services/                         # Core engine
│   │   ├── RuntimeAssemblyBuilder.cs     # Roslyn C# generation + compilation
│   │   ├── AssemblyGenerationManager.cs  # ALC lifecycle management
│   │   ├── SchemaSynchronizer.cs         # DDL via Npgsql
│   │   ├── SchemaChangeOrchestrator.cs   # Hot-load orchestration
│   │   ├── DynamicModelCacheKeyFactory.cs# EF Core model invalidation
│   │   ├── GraduationService.cs          # Source code export
│   │   └── SupportedTypes.cs             # Type mapping
│   ├── Controllers/                      # XAF actions
│   │   ├── SchemaChangeController.cs     # Deploy Schema
│   │   ├── GraduateController.cs         # Graduate
│   │   └── TestCompileController.cs      # Test Compile
│   ├── Validation/                       # Name validation rules
│   └── Module.cs                         # Bootstrap, metadata query
│
├── XafDynamicAssemblies.Blazor.Server/   # Blazor Server host
│   ├── Program.cs                        # Exit-code-42 restart mechanism
│   ├── Startup.cs                        # DI, XAF, SignalR wiring
│   ├── Services/RestartService.cs        # Restart request tracking
│   └── Hubs/SchemaUpdateHub.cs           # Client notifications
│
├── XafDynamicAssemblies.Win/             # WinForms host (Windows-only)
│
├── tests/                                # Playwright E2E tests (Python)
│   ├── conftest.py                       # Browser/page fixtures
│   ├── pages/                            # Page object models
│   │   ├── navigation_page.py            # XAF accordion nav
│   │   ├── list_view_page.py             # Grid interactions
│   │   └── detail_view_page.py           # Form interactions
│   └── tests/                            # 8 phases, 68 tests
│       ├── test_phase1_metadata_crud.py
│       ├── test_phase2_runtime_entities.py
│       ├── test_phase3_validation.py
│       ├── test_phase4_hot_load.py
│       ├── test_phase5_relationships.py
│       ├── test_phase6_graduation.py
│       ├── test_phase7_error_handling.py
│       └── test_phase8_performance.py
│
├── docker-compose.yml                    # PostgreSQL 17 + Python test runner
├── Dockerfile.python                     # Playwright test image
├── run-server.bat                        # Windows restart wrapper
└── run-server.sh                         # Linux restart wrapper
```

## Running Tests

The server must be running via `run-server.bat` / `run-server.sh` (not `dotnet run` directly) because tests trigger deploy+restart cycles.

```bash
# Full regression (68 tests, ~20 minutes)
docker exec xaf-dynamic-python bash -c \
  "cd /workspace && python3 -m pytest tests/tests/ -v --timeout=180"

# Single phase
docker exec xaf-dynamic-python bash -c \
  "cd /workspace && python3 -m pytest tests/tests/test_phase4_hot_load.py -v --timeout=180"
```

### Test Phases

| Phase | Tests | What It Covers |
|-------|-------|----------------|
| 1 — Metadata CRUD | 11 | Create, read, update, delete CustomClass and CustomField |
| 2 — Runtime Entities | 13 | Roslyn compilation, entity setup, full CRUD on runtime types |
| 3 — Validation | 9 | Invalid names, reserved words, type dropdown, test compile action |
| 4 — Hot-Load | 7 | Deploy action, navigation updates, field addition, data survival across restarts |
| 5 — Relationships | 8 | Entity references, FK constraints, cross-entity navigation |
| 6 — Graduation | 9 | Source generation, status transition, data preservation post-graduation |
| 7 — Error Handling | 7 | Degraded mode, compilation failure recovery, empty metadata, restart resilience |
| 8 — Performance | 4 | Bulk 10-class compilation, concurrent page access |

### Test Environment

| Variable | Default | Description |
|----------|---------|-------------|
| `BASE_URL` | `https://host.docker.internal:5001` | App URL from inside Docker |
| `HEADLESS` | `true` | Headless browser mode |
| `SLOW_MO` | `0` | Slow down for debugging (ms) |

## Database

PostgreSQL 17 runs via Docker on a non-standard port:

| Setting | Value |
|---------|-------|
| Host | `localhost` |
| Port | `5434` |
| Database | `XafDynamicAssemblies` |
| Username | `xafdynamic` |
| Password | `xafdynamic` |

```bash
# Start the database
docker compose up -d postgres

# Manual schema update
dotnet run --project XafDynamicAssemblies/XafDynamicAssemblies.Blazor.Server \
  -- --updateDatabase --forceUpdate --silent
```

## Architecture Deep Dive

For internals — the Roslyn compilation pipeline, hot-load sequence, type identity management, process restart mechanism, and graduation workflow — see **[UNDER_THE_HOOD.md](UNDER_THE_HOOD.md)**.

## Known Limitations

- **Non-collectible ALC** — runtime types persist in memory. Hot-load works via process restart, not in-process unload.
- **No inverse navigation** — compiled entities cannot have navigation properties pointing back to runtime entities.
- **Process restart required** — XAF's `TypesInfo` and `SharedApplicationModelManagerContainer` are process-static and cannot be reset in-process after recompilation.
- **DevExpress license required** — XAF is commercial software.

## License

This project uses DevExpress XAF, which requires a commercial license. The project code is provided as-is for educational and reference purposes.
