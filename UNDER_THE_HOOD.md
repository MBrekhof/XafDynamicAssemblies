# Under the Hood

A deep dive into how XafDynamicAssemblies turns metadata rows into running CLR types with full SQL backing.

## Table of Contents

- [The Big Picture](#the-big-picture)
- [Metadata Model](#metadata-model)
- [Roslyn Compilation Pipeline](#roslyn-compilation-pipeline)
- [Schema Synchronization (DDL)](#schema-synchronization-ddl)
- [Startup Bootstrap Sequence](#startup-bootstrap-sequence)
- [Hot-Load and Process Restart](#hot-load-and-process-restart)
- [EF Core Model Invalidation](#ef-core-model-invalidation)
- [Type Identity and the ALC Problem](#type-identity-and-the-alc-problem)
- [XAF Integration Points](#xaf-integration-points)
- [SignalR Client Reconnection](#signalr-client-reconnection)
- [Graduation Pipeline](#graduation-pipeline)
- [Error Handling and Degraded Mode](#error-handling-and-degraded-mode)
- [Validation Rules](#validation-rules)
- [Testing Architecture](#testing-architecture)
- [Lessons Learned](#lessons-learned)

---

## The Big Picture

The system has two metadata tables — `CustomClasses` and `CustomFields` — that describe runtime entity types. When "Deploy Schema" is clicked:

```
Metadata (DB rows)
    │
    ▼
C# Source Generation (RuntimeAssemblyBuilder)
    │
    ▼
Roslyn Compilation (in-memory .dll)
    │
    ▼
AssemblyLoadContext loads the assembly
    │
    ▼
DDL creates/alters PostgreSQL tables (SchemaSynchronizer)
    │
    ▼
EF Core model rebuilt (DynamicModelCacheKeyFactory)
    │
    ▼
XAF TypesInfo registers the types
    │
    ▼
Process restart (exit code 42)
    │
    ▼
Fresh process compiles everything from scratch
    │
    ▼
XAF auto-generates views (ListViews, DetailViews)
```

The key insight: **every deploy triggers a full process restart**. XAF's internal caches (`TypesInfo`, `SharedApplicationModelManagerContainer`) are process-static singletons that cannot be properly reset. Rather than fighting this, we embrace it — compile, restart, done.

---

## Metadata Model

### CustomClass

```csharp
public class CustomClass : BaseObject
{
    public virtual string ClassName { get; set; }          // "Invoice"
    public virtual string NavigationGroup { get; set; }    // "Billing"
    public virtual string Description { get; set; }
    public virtual CustomClassStatus Status { get; set; }  // Runtime | Compiled
    public virtual string GraduatedSource { get; set; }    // Generated C# after graduation
    public virtual IList<CustomField> Fields { get; set; } // [Aggregated] cascade
}
```

### CustomField

```csharp
public class CustomField : BaseObject
{
    public virtual Guid? CustomClassId { get; set; }       // Explicit FK (not shadow)
    public virtual CustomClass CustomClass { get; set; }
    public virtual string FieldName { get; set; }          // "TotalAmount"
    public virtual string TypeName { get; set; }           // "System.Decimal"
    public virtual bool IsRequired { get; set; }
    public virtual bool IsDefaultField { get; set; }
    public virtual string ReferencedClassName { get; set; } // For FK refs
    public virtual int SortOrder { get; set; }
}
```

**Design decisions:**

- `CustomClassId` is an explicit `Guid?` property (not a shadow property) because EF Core needs it for the composite unique index `(CustomClassId, FieldName)`.
- `Status` is stored as a string via `.HasConversion<string>()` — readable in the database and avoids integer-to-enum mapping issues.
- `[Aggregated]` on `Fields` tells XAF to cascade delete; `.OnDelete(DeleteBehavior.Cascade)` in Fluent API tells EF Core to cascade delete. Both are needed.

---

## Roslyn Compilation Pipeline

### Source Generation

`RuntimeAssemblyBuilder.GenerateSource(CustomClass cc)` generates a complete C# class:

```csharp
using System;
using DevExpress.ExpressApp;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace XafDynamicAssemblies.RuntimeEntities
{
    [DefaultClassOptions]
    [NavigationItem("Billing")]
    [DefaultProperty("InvoiceNumber")]
    public class Invoice : BaseObject
    {
        public virtual string InvoiceNumber { get; set; }
        public virtual decimal? TotalAmount { get; set; }
        public virtual DateTime? DueDate { get; set; }
        public virtual Guid? CustomerId { get; set; }
        [ForeignKey("CustomerId")]
        public virtual Customer Customer { get; set; }
    }
}
```

Key details:
- All classes inherit from `BaseObject` (XAF's EF Core base)
- `[DefaultClassOptions]` enables automatic view generation
- `[NavigationItem]` places the entity in the specified nav group
- `[DefaultProperty]` sets the display property (first `IsDefaultField`, or first string field, or first field)
- Reference fields generate both a `Guid? FKId` property and a navigation property with `[ForeignKey]`
- Value types are nullable when not required (`decimal?`, `DateTime?`, etc.)

### Compilation

All classes are compiled into a **single assembly** per compilation:

```csharp
var compilation = CSharpCompilation.Create(
    assemblyName: $"RuntimeEntities_{Guid.NewGuid():N}",
    syntaxTrees: syntaxTrees,        // One per CustomClass
    references: references,           // All loaded assemblies + TRUSTED_PLATFORM_ASSEMBLIES
    options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
);

using var ms = new MemoryStream();
var emitResult = compilation.Emit(ms);
```

Reference resolution is aggressive — it includes:
1. Everything in `TRUSTED_PLATFORM_ASSEMBLIES` (the .NET runtime's assembly list)
2. Every assembly currently loaded in `AppDomain.CurrentDomain` (catches DevExpress assemblies)

This ensures runtime entities can reference any type available to the host application.

### Assembly Loading

```csharp
ms.Seek(0, SeekOrigin.Begin);
var alc = new CollectibleLoadContext();  // Actually non-collectible
var assembly = alc.LoadFromStream(ms);
result.RuntimeTypes = assembly.GetExportedTypes();
```

The `CollectibleLoadContext` is named optimistically but uses `isCollectible: false`. Collectible ALCs don't work with EF Core's change tracking proxies (Castle DynamicProxy). Since we restart the process anyway, non-collectible is fine — the old assembly is abandoned when the process exits.

---

## Schema Synchronization (DDL)

`SchemaSynchronizer` runs **before** Roslyn compilation to ensure the database tables exist:

```
For each CustomClass with Status = Runtime:
  1. Check if table exists → CREATE TABLE if not
  2. For each CustomField:
     - Check if column exists → ALTER TABLE ADD COLUMN if not
     - Reference fields also get a FK constraint
```

DDL is executed via raw `NpgsqlCommand` — not through EF Core migrations. This is intentional:
- Migrations require design-time knowledge of the model
- Runtime entities don't exist at design time
- Raw DDL is simpler and more predictable for `ALTER TABLE ADD COLUMN`

**DDL failures are non-fatal.** If a table already has extra columns (from a previous compilation that included more fields), that's harmless. The Roslyn compilation will still succeed.

### Type Mapping

```csharp
"System.String"   → "text"
"System.Int32"    → "integer"
"System.Int64"    → "bigint"
"System.Decimal"  → "numeric(18,6)"
"System.Double"   → "double precision"
"System.Single"   → "real"
"System.Boolean"  → "boolean"
"System.DateTime" → "timestamp without time zone"
"System.Guid"     → "uuid"
"System.Byte[]"   → "bytea"
```

---

## Startup Bootstrap Sequence

When the server starts, `XafDynamicAssembliesModule.Setup()` calls `BootstrapRuntimeEntities()`:

```
1. RuntimeConnectionString set in Startup.cs from appsettings.json

2. QueryMetadata(connectionString)
   └── Raw Npgsql query: SELECT from CustomClasses WHERE Status = 'Runtime'
   └── Join CustomFields for each class
   └── Returns List<CustomClass> with populated Fields collections

3. SchemaSynchronizer.SynchronizeAll(classes)
   └── CREATE TABLE / ALTER TABLE for each class+field

4. AssemblyGenerationManager.LoadNewAssembly(classes)
   └── GenerateSource() for each class
   └── Roslyn compile → MemoryStream → ALC load
   └── Returns CompilationResult with Type[]

5. DbContext.RuntimeEntityTypes = runtimeTypes
   └── Static property setter increments ModelVersion
   └── Next DbContext creation will rebuild IModel

6. RefreshRuntimeTypes(runtimeTypes)
   └── Add to AdditionalExportedTypes (XAF type discovery)

7. SchemaChangeOrchestrator.SetKnownTypeNames(typeNames)
   └── Seeds baseline so first deploy doesn't false-positive RestartNeeded
```

**Why raw Npgsql instead of EF Core for metadata queries?** Because this runs inside `Module.Setup()`, before XAF has finished initializing the ObjectSpace infrastructure. EF Core isn't available yet. Raw SQL is the only option at this point in the lifecycle.

---

## Hot-Load and Process Restart

### The Deploy Schema Flow

```
User clicks "Deploy Schema" (SchemaChangeController)
    │
    ▼
SchemaChangeOrchestrator.ExecuteHotLoadAsync()
    │
    ├── QueryMetadata()        — fresh metadata from DB
    ├── SchemaSynchronizer()   — DDL sync
    ├── LoadNewAssembly()      — Roslyn compile
    ├── RuntimeEntityTypes =   — update DbContext (model invalidated)
    ├── RegisterTypesInTypesInfo() — inform XAF
    ├── RefreshRuntimeTypes()  — update AdditionalExportedTypes
    ├── RestartNeeded = true   — always true after compilation
    └── SchemaChanged event    — fires with version number
           │
           ▼
    Startup.cs event handler
    ├── SignalR broadcast to all clients
    └── Task.Run(async () => {
            await Task.Delay(3000);
            Environment.Exit(42);   // Force-exit
        });
```

### Why Environment.Exit(42)?

The original implementation used `IHostApplicationLifetime.StopApplication()` for graceful shutdown. This hung indefinitely because active Blazor SignalR connections prevented the host from completing shutdown. `Environment.Exit(42)` force-terminates the process immediately.

### The Wrapper Script

`run-server.bat` / `run-server.sh` runs in a loop:

```batch
:loop
dotnet run --project ... --no-build
if %ERRORLEVEL% EQU 42 (
    echo Restarting in 1 second...
    timeout /t 1 /nobreak >nul
    goto loop
)
```

Exit code 42 means "restart requested." Any other exit code breaks the loop.

### Why Not In-Process Restart?

We tried. XAF has two process-static caches that cannot be reset:

1. **`XafTypesInfo.Instance`** — the global type metadata registry. Even with `XafTypesInfo.HardReset()`, some internal state persists.
2. **`SharedApplicationModelManagerContainer`** — caches the application model (views, actions, navigation). It's an internal static class with no public reset method.

After a Roslyn recompilation, the new types have different CLR identities (different `Assembly`). XAF's caches still reference the old types. The result: `InvalidOperationException: Cannot create DbSet for entity type 'X' since it is of type 'X' but the generic type provided is of type 'X'` — two different CLR types with the same name from different assemblies.

Process restart is the only reliable solution.

---

## EF Core Model Invalidation

EF Core caches its `IModel` (the compiled representation of `OnModelCreating`). When runtime types change, we need to force a rebuild.

### DynamicModelCacheKeyFactory

```csharp
public class DynamicModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        return (context.GetType(), designTime,
                XafDynamicAssembliesEFCoreDbContext.ModelVersion);
    }
}
```

`ModelVersion` is an `int` that increments every time `RuntimeEntityTypes` is set. When the cache key changes, EF Core calls `OnModelCreating` again, which iterates the current `RuntimeEntityTypes` array:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    foreach (var type in RuntimeEntityTypes)
    {
        modelBuilder.Entity(type).ToTable(type.Name);
    }
    // ... rest of configuration
}
```

This is registered in `Startup.cs`:

```csharp
options.ReplaceService<IModelCacheKeyFactory, DynamicModelCacheKeyFactory>();
```

---

## Type Identity and the ALC Problem

### The Core Issue

.NET type identity is `(Assembly, Namespace, TypeName)`. Two types with the same full name from different assemblies are **different CLR types**. When Roslyn compiles a new assembly, every type in it is a brand-new type — even if the source code is identical to the previous compilation.

This means:
- `typeof(Invoice)` from Assembly v1 ≠ `typeof(Invoice)` from Assembly v2
- EF Core's model built with v1 types cannot be queried with v2 types
- XAF's TypesInfo registrations for v1 types don't apply to v2 types

### Our Solution

Single source of truth per process lifetime:

1. **One compilation per process** — `BootstrapRuntimeEntities` compiles once at startup
2. **Same types everywhere** — the `Type[]` from compilation is stored in both `RuntimeEntityTypes` (for EF Core) and `AdditionalExportedTypes` (for XAF)
3. **Process restart for changes** — never try to swap types in a running process

The `AssemblyGenerationManager` tracks the current compilation result. If `HasLoadedAssembly` is true during bootstrap, it reuses the existing types instead of recompiling (handles the case where `Setup()` is called twice).

---

## XAF Integration Points

### Type Registration

Runtime types are registered with XAF via `AdditionalExportedTypes` on the module:

```csharp
public void RefreshRuntimeTypes(Type[] runtimeTypes)
{
    foreach (var oldType in _addedRuntimeTypes)
        AdditionalExportedTypes.Remove(oldType);
    _addedRuntimeTypes.Clear();

    foreach (var type in runtimeTypes)
    {
        AdditionalExportedTypes.Add(type);
        _addedRuntimeTypes.Add(type);
    }
}
```

XAF then auto-generates:
- **ListView** with all properties as grid columns
- **DetailView** with property editors for each field
- **Navigation items** based on `[NavigationItem("GroupName")]`
- **Default property** display based on `[DefaultProperty("FieldName")]`

### Generated Class Attributes

Each runtime class gets:
- `[DefaultClassOptions]` — tells XAF to create views and nav items
- `[NavigationItem("GroupName")]` — sets the nav group
- `[DefaultProperty("Name")]` — sets the display/search property

### Controller Registration

Custom controllers (Deploy Schema, Graduate, Test Compile) are standard XAF controllers registered through the module system. They appear as toolbar actions on the appropriate views.

---

## SignalR Client Reconnection

When the server broadcasts `SchemaChanged` before shutting down, the client-side JavaScript handles reconnection:

```javascript
// _Host.cshtml
connection.on("SchemaChanged", function (version, needsRestart) {
    if (needsRestart) {
        setTimeout(pollAndReload, 3000);
    }
});

function pollAndReload() {
    var interval = setInterval(function () {
        fetch("/", { method: "HEAD" })
            .then(function (r) {
                if (r.ok) {
                    clearInterval(interval);
                    location.reload();
                }
            })
            .catch(function () { /* still down */ });
    }, 1000);
}
```

Additionally, Blazor's built-in reconnection handler is overridden to also poll:

```javascript
Blazor.reconnectionHandler = {
    onConnectionDown: function () {
        setTimeout(pollAndReload, 2000);
    },
    onConnectionUp: function () { }
};
```

The flow:
1. SignalR delivers `SchemaChanged` with `needsRestart=true`
2. Client waits 3 seconds (server needs time to exit)
3. Client polls HEAD requests every 1 second
4. When the new server responds, `location.reload()` gets a fresh page with the new types

---

## Graduation Pipeline

Graduation moves a runtime entity to compiled code:

### Step 1: Generate Source

`GraduationService.ExportSource()` creates three artifacts:

**1. C# class file:**
```csharp
// Generated from runtime entity 'Invoice'
// Graduated on 2026-03-04
using DevExpress.ExpressApp;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace YourNamespace
{
    [DefaultClassOptions]
    [NavigationItem("Billing")]
    [DefaultProperty("InvoiceNumber")]
    public class Invoice : BaseObject
    {
        public virtual string InvoiceNumber { get; set; }
        public virtual decimal? TotalAmount { get; set; }
    }
}
```

**2. DbContext snippet:**
```csharp
// Add to your DbContext:
public DbSet<Invoice> Invoices { get; set; }

// Add to OnModelCreating:
modelBuilder.Entity<Invoice>().ToTable("Invoice");
```

**3. Migration note:**
```
// The table 'Invoice' already exists in the database.
// No migration is needed — the compiled entity takes over the existing table.
```

### Step 2: Status Transition

`GraduateController` sets `Status = Compiled` and stores the generated source in `GraduatedSource`.

### Step 3: Next Deploy

On the next deploy + restart, `QueryMetadata()` filters `WHERE Status = 'Runtime'`. The graduated entity is excluded from Roslyn compilation. The SQL table and data remain untouched.

---

## Error Handling and Degraded Mode

### Degraded Mode

If Roslyn compilation fails at startup (e.g., bad metadata), the module enters degraded mode:

```csharp
public static bool DegradedMode { get; private set; }
public static string DegradedModeReason { get; private set; }
```

In degraded mode:
- Compiled entities (CustomClass, CustomField, etc.) work normally
- Runtime entity views are unavailable
- The admin can fix the bad metadata and redeploy

### Error Isolation

DDL and compilation have separate try/catch blocks:

```csharp
// DDL failure is non-fatal
try { schemaSyncer.SynchronizeAll(classes); }
catch (Exception ddlEx) { /* log and continue */ }

// Compilation failure triggers degraded mode
var result = AssemblyManager.LoadNewAssembly(classes);
if (!result.Success)
{
    DegradedMode = true;
    DegradedModeReason = "Roslyn compilation failed: " + errors;
    return;
}
```

### Recovery

Fix the metadata (remove the bad class/field), click Deploy Schema, and the next restart will compile successfully. No manual intervention beyond fixing the data.

---

## Validation Rules

### CustomClass Validation

- **Valid C# identifier** — letters, digits, underscores; cannot start with a digit
- **Not a C# keyword** — `class`, `int`, `string`, etc. are rejected
- **Not a reserved type** — `BaseObject`, `Object`, etc. are rejected
- **Unique class name** — enforced by database unique index with `GCRecord = 0` filter

### CustomField Validation

- **Valid C# identifier** — same rules as class names
- **Not a reserved field name** — `Id`, `ID`, `ObjectType`, `GCRecord`, `OptimisticLockField` (these are BaseObject properties)
- **Valid type name** — must be in the supported types list, or `Reference` with a `ReferencedClassName`
- **Unique per class** — composite unique index on `(CustomClassId, FieldName)` with `GCRecord = 0` filter

All validation uses `[RuleFromBoolProperty]` attributes — XAF's declarative validation system that fires on save.

---

## Testing Architecture

### Page Object Model

Tests use a Playwright-based Page Object Model tailored to DevExpress XAF Blazor:

```
BasePage          — wait_for_loading(), wait_for_view()
NavigationPage    — navigate_to("Schema Management", "Custom Class")
ListViewPage      — wait_for_grid(), click_new(), has_row_with_text(), click_delete()
DetailViewPage    — fill_field("Class Name", "Invoice"), click_save()
```

### DevExpress Blazor Gotchas

1. **Accordion expansion** — Playwright's `force=True` doesn't work with Blazor's event system. Must use `page.evaluate()` with native DOM `.click()`.

2. **Popup overlay** — `<dxbl-modal-root>` inside `<dxbl-popup-root>` intercepts all pointer events. Wait for it to disappear or click through it.

3. **Direct URL navigation** — After deploy+restart, `page.goto(f"{BASE_URL}/EntityName_ListView")` is more reliable than clicking nav links for runtime entity views.

4. **Display names** — XAF formats PascalCase as spaced words: `HotLoadProduct` becomes `Hot Load Product` in the UI.

### Test Isolation

Each phase creates its test entities, runs assertions, and cleans up in a `TestCleanup` class. The cleanup drops both the metadata rows and the SQL tables. This ensures phases don't interfere with each other, though they must run in order (later phases depend on the server state established by earlier phases).

### Fixture Design

```python
@pytest.fixture(scope="session")
def browser():       # One Chromium instance for all tests

@pytest.fixture(scope="function")
def context():       # Fresh browser context per test (clean cookies/state)

@pytest.fixture(scope="function")
def page():          # Navigates to BASE_URL and waits for XAF nav to load
```

---

## Lessons Learned

### 1. Process restart > in-process reset

We spent significant effort trying to reset XAF's TypesInfo and application model in-process. It doesn't work reliably. Process restart via exit code 42 is simple, deterministic, and fast (~3 seconds).

### 2. Environment.Exit() > StopApplication()

ASP.NET Core's graceful shutdown (`IHostApplicationLifetime.StopApplication()`) hangs when Blazor SignalR connections are active. `Environment.Exit(42)` is brutal but effective.

### 3. Non-collectible ALC is fine

Collectible `AssemblyLoadContext` sounds appealing for hot-swap scenarios, but it conflicts with EF Core's change tracking proxies (Castle DynamicProxy). Since we restart the process anyway, non-collectible works perfectly.

### 4. Raw SQL for bootstrap

Using EF Core during `Module.Setup()` isn't possible — the ObjectSpace infrastructure isn't ready yet. Raw Npgsql queries are the only option for reading metadata at this stage.

### 5. DDL before compilation

Schema sync must happen before Roslyn compilation. If tables don't exist when EF Core tries to query them, you get runtime errors. Extra columns (from fields that were later removed) are harmless.

### 6. Single assembly per compilation

All runtime classes go into one Roslyn assembly. This is necessary for cross-references between runtime entities (e.g., `Employee` referencing `Department`). If they were in separate assemblies, cross-references would fail.

### 7. Validation at the metadata level

Catching errors during metadata entry (invalid names, reserved words, unsupported types) is far better than catching them during Roslyn compilation. By the time Roslyn reports an error, the user has already saved their metadata and moved on.
