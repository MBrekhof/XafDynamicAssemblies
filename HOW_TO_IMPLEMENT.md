# How to Build Dynamic Runtime Entities for DevExpress XAF

A practical implementation guide for adding runtime entity creation to an existing XAF EF Core application. By the end of this guide, your users will be able to define new business object types, properties, and relationships at runtime -- without recompilation -- and the system will generate real CLR types backed by real SQL columns and foreign key constraints.

This is **not** an Entity-Attribute-Value pattern. It produces genuine C# classes via Roslyn compilation, loaded into a collectible `AssemblyLoadContext`. The result is indistinguishable from a compiled entity: proper column types, indexes, navigation properties, and full XAF ListView/DetailView support.

## What the End Result Looks Like

1. A user opens the XAF app, navigates to "Schema Management > Custom Class", and creates a new class called `EmployeeInformation` with navigation group "HR".
2. They add fields: `FirstName` (string), `Salary` (decimal), `HireDate` (datetime).
3. The system creates the PostgreSQL table, Roslyn-compiles a real C# class, registers it with XAF's TypesInfo, and a new "HR > Employee Information" navigation item appears.
4. The user navigates there and starts entering data -- standard XAF list view, detail view, filtering, sorting, export.
5. No restart required.

## Prerequisites

- .NET 8 or later
- DevExpress XAF 25.1+ with EF Core (the `BaseObject` EF Core path, not XPO)
- PostgreSQL (this guide uses PostgreSQL; adapt the DDL and type mappings for SQL Server if needed)
- NuGet packages beyond the standard XAF template:
  - `Microsoft.CodeAnalysis.CSharp` 4.10+
  - `Microsoft.CodeAnalysis.Common` 4.10+
  - `Npgsql.EntityFrameworkCore.PostgreSQL` 8.0+
  - `Microsoft.EntityFrameworkCore.Proxies` 8.0+

---

## Phase 1: Metadata Foundation

Everything starts with two tables: `CustomClass` (what entities exist) and `CustomField` (what properties they have). These are regular XAF business objects that users interact with through the standard UI.

### Step 1: Create the CustomClassStatus Enum

```csharp
public enum CustomClassStatus
{
    Runtime,      // Active runtime entity, managed by Roslyn
    Graduating,   // Being exported to compiled code
    Compiled      // Graduated -- now a regular compiled class
}
```

The `Graduating` and `Compiled` statuses support the graduation workflow (Phase 7). For Phase 1, everything stays `Runtime`.

### Step 2: Create the CustomClass Entity

File: `Module/BusinessObjects/CustomClass.cs`

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace YourApp.Module.BusinessObjects
{
    public enum CustomClassStatus
    {
        Runtime,
        Graduating,
        Compiled
    }

    [DefaultClassOptions]
    [NavigationItem("Schema Management")]
    [DefaultProperty(nameof(ClassName))]
    public class CustomClass : BaseObject
    {
        public virtual string ClassName { get; set; }
        public virtual string NavigationGroup { get; set; }
        public virtual string Description { get; set; }
        public virtual CustomClassStatus Status { get; set; } = CustomClassStatus.Runtime;

        [Aggregated]
        public virtual IList<CustomField> Fields { get; set; } = new ObservableCollection<CustomField>();
    }
}
```

Key attributes explained:

- **`[DefaultClassOptions]`** -- Tells XAF to auto-generate a ListView and DetailView for this class. Without this, you would need to manually define views in the model.
- **`[NavigationItem("Schema Management")]`** -- Places this entity under a "Schema Management" group in the navigation pane. All your metadata management classes should live in a dedicated group to keep them separate from the business data.
- **`[DefaultProperty(nameof(ClassName))]`** -- When this object appears as a reference in another entity (e.g., in a lookup), XAF will display the `ClassName` value. Without this, it would show the `ToString()` representation.
- **`[Aggregated]`** on `Fields` -- This is an XAF attribute (not EF Core). It tells XAF that `CustomField` objects are owned by their parent `CustomClass`. When you delete a `CustomClass` in the UI, XAF will cascade-delete its fields in the same unit of work. This is XAF-level cascade logic, separate from the EF Core FK cascade you configure in `OnModelCreating`.
- **`ObservableCollection<CustomField>`** -- Required for XAF change tracking proxies. A plain `List<T>` will not trigger change notifications.

### Step 3: Create the CustomField Entity

File: `Module/BusinessObjects/CustomField.cs`

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace YourApp.Module.BusinessObjects
{
    [DefaultClassOptions]
    [NavigationItem("Schema Management")]
    [DefaultProperty(nameof(FieldName))]
    public class CustomField : BaseObject
    {
        [ForeignKey(nameof(CustomClass))]
        public virtual Guid? CustomClassId { get; set; }
        public virtual CustomClass CustomClass { get; set; }
        public virtual string FieldName { get; set; }
        public virtual string TypeName { get; set; } = "System.String";
        public virtual bool IsRequired { get; set; }
        public virtual bool IsDefaultField { get; set; }
        public virtual string Description { get; set; }
        public virtual string ReferencedClassName { get; set; }
        public virtual int SortOrder { get; set; }
    }
}
```

**The explicit FK pattern:** Notice `CustomClassId` is declared as `Guid?` (nullable) with `[ForeignKey(nameof(CustomClass))]`. This is deliberate. XAF with EF Core change tracking proxies requires explicit FK properties for reliable behavior. If you let EF Core create a shadow FK property, you will hit issues when:

- XAF tries to set the FK value during object creation in a nested detail view
- The change tracking proxy cannot detect changes to a shadow property
- Composite unique indexes reference the FK column

The `Guid?` (nullable) allows creating a `CustomField` without immediately assigning it to a class -- useful for standalone list views where users create fields first and link them later.

**`ReferencedClassName`** stores the target class name for FK relationship fields (Phase 6). For simple scalar fields, it is null.

### Step 4: Create the SupportedTypes Helper

File: `Module/Services/SupportedTypes.cs`

This class maps CLR type names to PostgreSQL column types. It is the single source of truth for what types your dynamic entities can use.

```csharp
namespace YourApp.Module.Services
{
    public static class SupportedTypes
    {
        private static readonly Dictionary<string, string> ClrToPostgres = new()
        {
            ["System.String"]   = "text",
            ["System.Int32"]    = "integer",
            ["System.Int64"]    = "bigint",
            ["System.Decimal"]  = "numeric(18,6)",
            ["System.Double"]   = "double precision",
            ["System.Single"]   = "real",
            ["System.Boolean"]  = "boolean",
            ["System.DateTime"] = "timestamp without time zone",
            ["System.Guid"]     = "uuid",
            ["System.Byte[]"]   = "bytea",
        };

        public static IReadOnlyList<string> AllTypeNames => ClrToPostgres.Keys.ToList();

        public static string GetPostgresType(string clrTypeName)
        {
            if (ClrToPostgres.TryGetValue(clrTypeName, out var pgType))
                return pgType;
            throw new ArgumentException($"Unsupported CLR type: {clrTypeName}");
        }

        public static bool IsSupported(string clrTypeName)
        {
            return ClrToPostgres.ContainsKey(clrTypeName);
        }

        public static string GetPostgresDefault(string clrTypeName)
        {
            return clrTypeName switch
            {
                "System.String" => "''",
                "System.Int32" or "System.Int64" or "System.Single" or "System.Double" => "0",
                "System.Decimal" => "0",
                "System.Boolean" => "false",
                "System.DateTime" => "CURRENT_TIMESTAMP",
                "System.Guid" => "gen_random_uuid()",
                _ => "NULL"
            };
        }
    }
}
```

**If you are using SQL Server instead of PostgreSQL**, replace the mapping:

```csharp
["System.String"]   = "nvarchar(max)",
["System.Int32"]    = "int",
["System.Int64"]    = "bigint",
["System.Decimal"]  = "decimal(18,6)",
["System.Double"]   = "float",
["System.Boolean"]  = "bit",
["System.DateTime"] = "datetime2",
["System.Guid"]     = "uniqueidentifier",
["System.Byte[]"]   = "varbinary(max)",
```

### Step 5: Configure the DbContext

File: `Module/BusinessObjects/YourAppDbContext.cs`

Add the `DbSet` declarations, the `RuntimeEntityTypes` static property, and the `OnModelCreating` configuration.

```csharp
public DbSet<CustomClass> CustomClasses { get; set; }
public DbSet<CustomField> CustomFields { get; set; }

/// <summary>
/// Runtime entity types compiled by Roslyn. Set by AssemblyGenerationManager
/// at startup and during hot-load. Static because OnModelCreating has no
/// access to DI -- this is the only clean way to feed dynamic types into
/// the EF Core model.
/// </summary>
public static Type[] RuntimeEntityTypes { get; set; } = Array.Empty<Type>();
```

**Why is `RuntimeEntityTypes` static?** EF Core's `OnModelCreating` runs during `DbContext` construction. At that point, you have no DI scope, no service provider, no way to inject a list of types. A static property is the pragmatic solution. You set it before any `DbContext` instances are created, and `OnModelCreating` reads it.

In `OnModelCreating`, add the following after your existing XAF configuration lines:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);

    // Standard XAF EF Core configuration
    modelBuilder.UseDeferredDeletion(this);
    modelBuilder.UseOptimisticLock();
    modelBuilder.SetOneToManyAssociationDeleteBehavior(
        DeleteBehavior.SetNull, DeleteBehavior.Cascade);
    modelBuilder.HasChangeTrackingStrategy(
        ChangeTrackingStrategy.ChangingAndChangedNotificationsWithOriginalValues);
    modelBuilder.UsePropertyAccessMode(PropertyAccessMode.PreferFieldDuringConstruction);

    // CustomClass configuration
    modelBuilder.Entity<CustomClass>(entity =>
    {
        entity.HasIndex(e => e.ClassName).IsUnique();
        entity.Property(e => e.ClassName).HasMaxLength(128).IsRequired();
        entity.Property(e => e.NavigationGroup).HasMaxLength(128);
        entity.Property(e => e.Status)
            .HasConversion<string>()   // Store as "Runtime", not 0
            .HasMaxLength(20)
            .HasDefaultValue(CustomClassStatus.Runtime);
        entity.HasMany(e => e.Fields)
            .WithOne(f => f.CustomClass)
            .OnDelete(DeleteBehavior.Cascade);
    });

    // CustomField configuration
    modelBuilder.Entity<CustomField>(entity =>
    {
        entity.HasIndex(e => new { e.CustomClassId, e.FieldName }).IsUnique();
        entity.Property(e => e.FieldName).HasMaxLength(128).IsRequired();
        entity.Property(e => e.TypeName).HasMaxLength(256).HasDefaultValue("System.String");
        entity.Property(e => e.ReferencedClassName).HasMaxLength(128);
    });

    // Register runtime entity types -- this is where the magic happens
    foreach (var type in RuntimeEntityTypes)
    {
        modelBuilder.Entity(type).ToTable(type.Name);
    }
}
```

Key configuration details:

- **`.HasConversion<string>()`** on `Status` -- Stores the enum as its string name ("Runtime", "Graduating", "Compiled") rather than an integer. This makes the database readable and survives enum reordering.
- **Composite unique index** on `(CustomClassId, FieldName)` -- Prevents duplicate field names within the same class.
- **`DeleteBehavior.Cascade`** on the FK -- When a `CustomClass` row is deleted in the database, its `CustomField` rows are cascade-deleted at the database level. This is separate from the XAF `[Aggregated]` cascade, which operates at the ORM level.
- **The `RuntimeEntityTypes` loop** -- For each type that Roslyn has compiled, we call `modelBuilder.Entity(type).ToTable(type.Name)`. This tells EF Core "this type is an entity, map it to a table with the same name as the class." Without this, EF Core would not know about the runtime types.

### Step 6: Register in Module.cs

In your module's constructor, add the metadata types to `AdditionalExportedTypes`:

```csharp
public sealed class YourAppModule : ModuleBase
{
    public YourAppModule()
    {
        // ... existing RequiredModuleTypes ...

        AdditionalExportedTypes.Add(typeof(BusinessObjects.CustomClass));
        AdditionalExportedTypes.Add(typeof(BusinessObjects.CustomField));
    }
}
```

`AdditionalExportedTypes` tells XAF's TypesInfo system to include these types even though they may not be automatically discovered through reflection. This guarantees they appear in the navigation and have proper views generated.

### Step 7: PostgreSQL-Specific Setup in Startup.cs

In your Blazor Server `Startup.cs` (or wherever you configure the EF Core provider), there are three PostgreSQL-specific settings:

```csharp
builder.ObjectSpaceProviders
    .AddEFCore(options =>
    {
        options.PreFetchReferenceProperties();
    })
    .WithDbContext<YourAppDbContext>((serviceProvider, options) =>
    {
        string connectionString = Configuration.GetConnectionString("ConnectionString");
        ArgumentNullException.ThrowIfNull(connectionString);
        options.UseNpgsql(connectionString);
        options.UseChangeTrackingProxies();
        options.UseLazyLoadingProxies();
    })
    .AddNonPersistent();
```

**`UseNpgsql`** -- If you are migrating from the XAF template (which defaults to SQL Server), you must replace `UseSqlServer` with `UseNpgsql`. The XAF template's `UseConnectionString` helper will always try SQL Server.

**`UseChangeTrackingProxies()`** -- Required for XAF EF Core. XAF depends on `INotifyPropertyChanged` / `INotifyPropertyChanging` events from entities. The change tracking proxy wraps your entity and fires these events when properties change. Without this, XAF's UI will not react to programmatic changes.

**`UseLazyLoadingProxies()`** -- Enables lazy loading of navigation properties. When you access `customField.CustomClass`, EF Core will automatically load the related `CustomClass` from the database. This is what makes the `virtual` keyword on properties meaningful.

**Npgsql legacy timestamp behavior** -- Add this at the very start of your application (e.g., top of `Program.cs` or `Startup.ConfigureServices`):

```csharp
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
```

Without this, Npgsql 6+ will throw exceptions when you try to store `DateTime` values (as opposed to `DateTimeOffset`). XAF's `BaseObject` and many DevExpress types use `DateTime`, so you need this switch.

**Connection string format** for PostgreSQL:

```json
{
  "ConnectionStrings": {
    "ConnectionString": "Host=localhost;Port=5434;Database=YourDb;Username=youruser;Password=yourpass"
  }
}
```

### Verify Phase 1

At this point you should be able to:

1. `dotnet build` successfully
2. Run the app and see "Schema Management" in the navigation
3. Create `CustomClass` and `CustomField` records through the UI
4. Verify cascade delete works (deleting a class deletes its fields)

No runtime entities yet -- that comes next.

---

## Phase 2: Roslyn Compilation

This is the core of the system. You will build a service that reads `CustomClass` + `CustomField` metadata from the database, generates C# source code, compiles it with Roslyn into an in-memory assembly, and loads it into a collectible `AssemblyLoadContext`.

### Step 1: RuntimeAssemblyBuilder -- Generating C# Source

File: `Module/Services/RuntimeAssemblyBuilder.cs`

The builder has two jobs:
1. Generate C# source code for each `CustomClass`
2. Compile all classes into a single assembly

**Source code generation** -- For each `CustomClass`, generate a class that:
- Lives in a known namespace (e.g., `YourApp.DynamicEntities`)
- Inherits from `BaseObject`
- Has `[DefaultClassOptions]` and `[NavigationItem]` attributes
- Has `virtual` properties for each `CustomField` (required for change tracking proxies)

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;
using System.Runtime.Loader;

namespace YourApp.Module.Services
{
    public class RuntimeAssemblyBuilder
    {
        private const string DynamicNamespace = "YourApp.DynamicEntities";

        /// <summary>
        /// Generate C# source code for a single CustomClass.
        /// </summary>
        public string GenerateClassSource(CustomClass customClass)
        {
            var sb = new StringBuilder();

            sb.AppendLine("using System;");
            sb.AppendLine("using System.ComponentModel;");
            sb.AppendLine("using DevExpress.ExpressApp.DC;");
            sb.AppendLine("using DevExpress.Persistent.Base;");
            sb.AppendLine("using DevExpress.Persistent.BaseImpl.EF;");
            sb.AppendLine();
            sb.AppendLine($"namespace {DynamicNamespace}");
            sb.AppendLine("{");

            // Class attributes
            sb.AppendLine("    [DefaultClassOptions]");

            if (!string.IsNullOrWhiteSpace(customClass.NavigationGroup))
            {
                sb.AppendLine($"    [NavigationItem(\"{customClass.NavigationGroup}\")]");
            }

            // Use the first string field as DefaultProperty, or ClassName as fallback
            var defaultPropField = customClass.Fields
                .Where(f => f.TypeName == "System.String" && !f.IsDefaultField)
                .OrderBy(f => f.SortOrder)
                .FirstOrDefault();

            if (defaultPropField != null)
            {
                sb.AppendLine($"    [DefaultProperty(nameof({defaultPropField.FieldName}))]");
            }

            sb.AppendLine($"    public class {customClass.ClassName} : BaseObject");
            sb.AppendLine("    {");

            // Generate properties for each field
            foreach (var field in customClass.Fields.OrderBy(f => f.SortOrder))
            {
                GenerateProperty(sb, field);
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private void GenerateProperty(StringBuilder sb, CustomField field)
        {
            string clrType = field.TypeName;

            // For reference fields, the property type is the referenced class
            if (!string.IsNullOrWhiteSpace(field.ReferencedClassName))
            {
                // FK property (Guid?)
                sb.AppendLine($"        public virtual Guid? {field.FieldName}Id {{ get; set; }}");
                // Navigation property
                sb.AppendLine($"        public virtual {DynamicNamespace}.{field.ReferencedClassName} {field.FieldName} {{ get; set; }}");
            }
            else
            {
                // Scalar property
                // Make value types nullable unless required, so columns are nullable by default
                string typeSuffix = "";
                if (!field.IsRequired && IsValueType(clrType))
                {
                    typeSuffix = "?";
                }

                sb.AppendLine($"        public virtual {GetShortTypeName(clrType)}{typeSuffix} {field.FieldName} {{ get; set; }}");
            }
        }

        private static bool IsValueType(string clrTypeName)
        {
            return clrTypeName switch
            {
                "System.Int32" or "System.Int64" or "System.Decimal"
                or "System.Double" or "System.Single" or "System.Boolean"
                or "System.DateTime" or "System.Guid" => true,
                _ => false
            };
        }

        private static string GetShortTypeName(string clrTypeName)
        {
            return clrTypeName switch
            {
                "System.String"   => "string",
                "System.Int32"    => "int",
                "System.Int64"    => "long",
                "System.Decimal"  => "decimal",
                "System.Double"   => "double",
                "System.Single"   => "float",
                "System.Boolean"  => "bool",
                "System.DateTime" => "DateTime",
                "System.Guid"     => "Guid",
                "System.Byte[]"   => "byte[]",
                _ => clrTypeName
            };
        }

        // ... compilation methods in the next step
    }
}
```

### Step 2: Setting Up MetadataReferences

Roslyn needs to know about all assemblies that the generated code references. This includes the BCL, EF Core, DevExpress, and your own module assembly.

```csharp
private static List<MetadataReference> GetMetadataReferences()
{
    var references = new List<MetadataReference>();

    // Core runtime assemblies -- find them via known types
    var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
        .Split(Path.PathSeparator);

    // Add all trusted platform assemblies (BCL + framework)
    // In production, you might filter this for performance, but for
    // correctness, include them all.
    foreach (var assemblyPath in trustedAssemblies)
    {
        references.Add(MetadataReference.CreateFromFile(assemblyPath));
    }

    // DevExpress assemblies -- find via types we know are in them
    var dxAssemblies = new[]
    {
        typeof(DevExpress.Persistent.Base.DefaultClassOptionsAttribute).Assembly,
        typeof(DevExpress.Persistent.BaseImpl.EF.BaseObject).Assembly,
        typeof(DevExpress.ExpressApp.DC.XafDefaultPropertyAttribute).Assembly,
    };

    foreach (var asm in dxAssemblies)
    {
        if (!string.IsNullOrEmpty(asm.Location))
        {
            references.Add(MetadataReference.CreateFromFile(asm.Location));
        }
    }

    return references;
}
```

**Why `TRUSTED_PLATFORM_ASSEMBLIES`?** In .NET Core, there is no GAC. The runtime publishes its list of assemblies via this `AppContext` data key. It includes `System.Runtime.dll`, `System.Collections.dll`, `netstandard.dll`, and everything else the app has loaded. This is the most reliable way to get all necessary BCL references.

**Why add DevExpress assemblies explicitly?** They are not in the trusted platform assemblies list -- they are NuGet packages loaded from your app's output directory. You need to find them through their types.

### Step 3: CSharpCompilation Setup

```csharp
public Assembly CompileToAssembly(IEnumerable<CustomClass> classes)
{
    // Generate source for all classes
    var syntaxTrees = new List<SyntaxTree>();
    foreach (var customClass in classes)
    {
        string source = GenerateClassSource(customClass);
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.CSharp12));
        syntaxTrees.Add(syntaxTree);
    }

    if (syntaxTrees.Count == 0)
        return null; // No runtime classes to compile

    // Unique assembly name per compilation (supports unloading/reloading)
    string assemblyName = $"DynamicEntities_{DateTime.UtcNow.Ticks}";

    var compilation = CSharpCompilation.Create(
        assemblyName,
        syntaxTrees: syntaxTrees,
        references: GetMetadataReferences(),
        options: new CSharpCompilationOptions(
            OutputKind.DynamicLibrary,
            optimizationLevel: OptimizationLevel.Release));

    // Emit to a MemoryStream
    using var ms = new MemoryStream();
    var result = compilation.Emit(ms);

    if (!result.Success)
    {
        var errors = result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.GetMessage())
            .ToList();

        throw new InvalidOperationException(
            $"Roslyn compilation failed:\n{string.Join("\n", errors)}");
    }

    ms.Seek(0, SeekOrigin.Begin);

    // Load into a collectible AssemblyLoadContext
    var alc = new CollectibleLoadContext(assemblyName);
    var assembly = alc.LoadFromStream(ms);

    return assembly;
}
```

**`OutputKind.DynamicLibrary`** -- We are producing a DLL, not an executable.

**`OptimizationLevel.Release`** -- No point generating debug symbols for runtime-generated code. Release mode is smaller and faster.

**`LanguageVersion.CSharp12`** -- Match your project's language version. If you are on .NET 8, C# 12 is the default.

### Step 4: Collectible AssemblyLoadContext

The ALC must be collectible so you can unload it during hot-reload (Phase 5). Here is a minimal implementation:

```csharp
public class CollectibleLoadContext : AssemblyLoadContext
{
    public CollectibleLoadContext(string name)
        : base(name, isCollectible: true)
    {
    }

    protected override Assembly Load(AssemblyName assemblyName)
    {
        // Return null to fall back to the default context.
        // The generated code references assemblies already loaded
        // in the default context (BCL, DevExpress, etc.), so we
        // do not need to resolve them here.
        return null;
    }
}
```

**Why collectible?** When a user adds a new field and you need to recompile, you must unload the old assembly. Non-collectible ALCs cannot be unloaded -- they live for the lifetime of the process. Collectible ALCs can be unloaded once all references to their types are released.

### Step 5: Error Handling

Roslyn compilation can fail for many reasons: invalid class names, reserved keywords used as property names, missing references. Always filter diagnostics to `DiagnosticSeverity.Error` -- warnings and info messages are noise.

```csharp
if (!result.Success)
{
    var errors = result.Diagnostics
        .Where(d => d.Severity == DiagnosticSeverity.Error)
        .Select(d => $"  {d.Id}: {d.GetMessage()} (Line {d.Location.GetLineSpan().StartLinePosition.Line + 1})")
        .ToList();

    // Log the generated source for debugging
    foreach (var tree in syntaxTrees)
    {
        Console.WriteLine("--- Generated Source ---");
        Console.WriteLine(tree.ToString());
    }

    throw new InvalidOperationException(
        $"Roslyn compilation failed with {errors.Count} error(s):\n{string.Join("\n", errors)}");
}
```

Logging the generated source on failure is essential. The most common errors are:
- Typos in class names that produce invalid C# identifiers
- Missing `using` directives
- Type reference mismatches when a field references a class that does not exist

---

## Phase 3: Schema Synchronization

Before you can use a runtime entity, the PostgreSQL table must exist. The `SchemaSynchronizer` creates and alters tables using raw DDL -- it does not go through EF Core migrations.

### Why Not EF Core Migrations?

EF Core migrations are designed for compile-time schema changes. They produce migration files that are part of your source code. For runtime entities, you need immediate DDL execution without generating migration files. Direct DDL is the right tool here.

### Step 1: Build SchemaSynchronizer

File: `Module/Services/SchemaSynchronizer.cs`

```csharp
using Npgsql;

namespace YourApp.Module.Services
{
    public class SchemaSynchronizer
    {
        private readonly string _connectionString;

        public SchemaSynchronizer(string connectionString)
        {
            _connectionString = connectionString;
        }

        /// <summary>
        /// Ensure the table exists for a CustomClass with all its fields.
        /// Creates the table if missing, adds any new columns if the table exists.
        /// </summary>
        public void SynchronizeClass(CustomClass customClass)
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();

            string tableName = customClass.ClassName;

            if (!TableExists(conn, tableName))
            {
                CreateTable(conn, customClass);
            }
            else
            {
                AddMissingColumns(conn, customClass);
            }
        }

        private bool TableExists(NpgsqlConnection conn, string tableName)
        {
            using var cmd = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM information_schema.tables " +
                "WHERE table_schema = 'public' AND table_name = @name)", conn);
            cmd.Parameters.AddWithValue("name", tableName);
            return (bool)cmd.ExecuteScalar();
        }

        private void CreateTable(NpgsqlConnection conn, CustomClass customClass)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"CREATE TABLE \"{customClass.ClassName}\" (");

            // BaseObject columns -- required by XAF EF Core
            sb.AppendLine("    \"Id\" uuid NOT NULL DEFAULT gen_random_uuid(),");
            sb.AppendLine("    \"ObjectType\" text NULL,");
            sb.AppendLine("    \"GCRecord\" integer NULL,");
            sb.AppendLine("    \"OptimisticLockField\" integer NULL,");

            // Custom columns
            foreach (var field in customClass.Fields)
            {
                if (!string.IsNullOrWhiteSpace(field.ReferencedClassName))
                {
                    // FK column -- always uuid, nullable
                    sb.AppendLine($"    \"{field.FieldName}Id\" uuid NULL,");
                }
                else
                {
                    string pgType = SupportedTypes.GetPostgresType(field.TypeName);
                    string nullable = field.IsRequired ? "NOT NULL" : "NULL";
                    string defaultVal = field.IsRequired
                        ? $" DEFAULT {SupportedTypes.GetPostgresDefault(field.TypeName)}"
                        : "";
                    sb.AppendLine($"    \"{field.FieldName}\" {pgType} {nullable}{defaultVal},");
                }
            }

            sb.AppendLine($"    CONSTRAINT \"PK_{customClass.ClassName}\" PRIMARY KEY (\"Id\")");
            sb.AppendLine(");");

            // Create GCRecord index (used by XAF deferred deletion)
            sb.AppendLine($"CREATE INDEX \"IX_{customClass.ClassName}_GCRecord\" " +
                          $"ON \"{customClass.ClassName}\" (\"GCRecord\");");

            using var cmd = new NpgsqlCommand(sb.ToString(), conn);
            cmd.ExecuteNonQuery();
        }

        private void AddMissingColumns(NpgsqlConnection conn, CustomClass customClass)
        {
            var existingColumns = GetExistingColumns(conn, customClass.ClassName);

            foreach (var field in customClass.Fields)
            {
                string columnName = !string.IsNullOrWhiteSpace(field.ReferencedClassName)
                    ? $"{field.FieldName}Id"
                    : field.FieldName;

                if (existingColumns.Contains(columnName, StringComparer.OrdinalIgnoreCase))
                    continue; // Column already exists

                string pgType = !string.IsNullOrWhiteSpace(field.ReferencedClassName)
                    ? "uuid"
                    : SupportedTypes.GetPostgresType(field.TypeName);

                string sql = $"ALTER TABLE \"{customClass.ClassName}\" " +
                             $"ADD COLUMN \"{columnName}\" {pgType} NULL";

                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.ExecuteNonQuery();
            }
        }

        private HashSet<string> GetExistingColumns(NpgsqlConnection conn, string tableName)
        {
            var columns = new HashSet<string>();
            using var cmd = new NpgsqlCommand(
                "SELECT column_name FROM information_schema.columns " +
                "WHERE table_schema = 'public' AND table_name = @name", conn);
            cmd.Parameters.AddWithValue("name", tableName);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(0));
            }
            return columns;
        }
    }
}
```

### BaseObject Columns

Every table for a `BaseObject`-derived entity needs these columns:

| Column | Type | Purpose |
|--------|------|---------|
| `Id` | uuid PK | Primary key, auto-generated |
| `ObjectType` | text | Discriminator for inheritance hierarchies (XAF uses this) |
| `GCRecord` | integer | Deferred deletion marker. When XAF "deletes" an object, it sets `GCRecord` to a non-null value instead of actually deleting the row. A background process eventually purges these rows. |
| `OptimisticLockField` | integer | Concurrency token. XAF increments this on each save and checks it on update to detect concurrent edits. |

### The "Never Drop Columns" Rule

Notice that `AddMissingColumns` only adds columns -- it never removes them. This is intentional:

1. **Data safety** -- Dropping a column destroys data irreversibly.
2. **Rollback support** -- If a user removes a field and then re-adds it, the data is still there.
3. **Concurrent access** -- Other connections might be reading the column during the DDL.

Orphaned columns are harmless. PostgreSQL does not care about extra columns that no entity maps to.

---

## Phase 4: Wiring It All Together at Startup

This is where you connect the metadata, the schema synchronizer, and the Roslyn compiler. The wiring happens in your module's `Setup(XafApplication)` override.

### The Startup Sequence

The order of operations is critical. Here is the sequence and why each step must happen when it does:

```
1. Query metadata (CustomClass + CustomField) from the database
2. Run SchemaSynchronizer for each class (CREATE/ALTER TABLE)
3. Roslyn-compile all classes into one assembly
4. Set DbContext.RuntimeEntityTypes to the compiled types
5. Register types in XAF's TypesInfo
6. Add types to AdditionalExportedTypes
```

### Step-by-Step Implementation

In `Module.cs`, override `Setup(XafApplication)`:

```csharp
public override void Setup(XafApplication application)
{
    base.Setup(application);

    try
    {
        LoadRuntimeEntities(application);
    }
    catch (Exception ex)
    {
        // Degraded mode -- app starts without dynamic entities.
        // Log the error but do not crash the application.
        System.Diagnostics.Trace.TraceError(
            $"Failed to load runtime entities: {ex.Message}");
    }
}

private void LoadRuntimeEntities(XafApplication application)
{
    // Step 1: Get the connection string
    // The application's ConnectionString is available at this point
    string connectionString = GetConnectionString(application);
    if (string.IsNullOrEmpty(connectionString))
        return;

    // Step 2: Query metadata directly via Npgsql (not through XAF ObjectSpace,
    // which is not fully initialized yet at this point)
    var classes = QueryCustomClasses(connectionString);
    if (classes.Count == 0)
        return;

    // Step 3: Synchronize database schema
    var schemaSyncer = new SchemaSynchronizer(connectionString);
    foreach (var customClass in classes)
    {
        schemaSyncer.SynchronizeClass(customClass);
    }

    // Step 4: Compile all classes with Roslyn
    var builder = new RuntimeAssemblyBuilder();
    var assembly = builder.CompileToAssembly(classes);
    if (assembly == null)
        return;

    // Step 5: Get the compiled types
    var runtimeTypes = assembly.GetExportedTypes();

    // Step 6: Set the static property BEFORE any DbContext is created
    XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes = runtimeTypes;

    // Step 7: Register types in XAF's TypesInfo
    foreach (var type in runtimeTypes)
    {
        XafTypesInfo.Instance.RegisterEntity(type);
        AdditionalExportedTypes.Add(type);
    }
}
```

### Why Query Directly via Npgsql?

At the point where `Setup(XafApplication)` runs, XAF's ObjectSpace providers are not fully initialized. You cannot call `application.CreateObjectSpace()` yet. So you query the metadata tables directly:

```csharp
private List<CustomClass> QueryCustomClasses(string connectionString)
{
    var classes = new List<CustomClass>();

    using var conn = new NpgsqlConnection(connectionString);
    conn.Open();

    // Check if the table exists (first run, before XAF creates it)
    using var checkCmd = new NpgsqlCommand(
        "SELECT EXISTS (SELECT 1 FROM information_schema.tables " +
        "WHERE table_schema = 'public' AND table_name = 'CustomClasses')", conn);

    if (!(bool)checkCmd.ExecuteScalar())
        return classes; // Table does not exist yet

    // Query classes
    using var classCmd = new NpgsqlCommand(
        "SELECT \"Id\", \"ClassName\", \"NavigationGroup\", \"Description\", \"Status\" " +
        "FROM \"CustomClasses\" WHERE \"Status\" = 'Runtime'", conn);

    var classMap = new Dictionary<Guid, CustomClass>();
    using (var reader = classCmd.ExecuteReader())
    {
        while (reader.Read())
        {
            var cc = new CustomClass
            {
                ClassName = reader.GetString(1),
                NavigationGroup = reader.IsDBNull(2) ? null : reader.GetString(2),
                Description = reader.IsDBNull(3) ? null : reader.GetString(3),
            };
            var id = reader.GetGuid(0);
            classMap[id] = cc;
            classes.Add(cc);
        }
    }

    // Query fields for all classes
    using var fieldCmd = new NpgsqlCommand(
        "SELECT \"CustomClassId\", \"FieldName\", \"TypeName\", \"IsRequired\", " +
        "\"IsDefaultField\", \"ReferencedClassName\", \"SortOrder\", \"Description\" " +
        "FROM \"CustomFields\" ORDER BY \"SortOrder\"", conn);

    using (var reader = fieldCmd.ExecuteReader())
    {
        while (reader.Read())
        {
            var classId = reader.IsDBNull(0) ? (Guid?)null : reader.GetGuid(0);
            if (classId == null || !classMap.ContainsKey(classId.Value))
                continue;

            var field = new CustomField
            {
                FieldName = reader.GetString(1),
                TypeName = reader.IsDBNull(2) ? "System.String" : reader.GetString(2),
                IsRequired = reader.GetBoolean(3),
                IsDefaultField = reader.GetBoolean(4),
                ReferencedClassName = reader.IsDBNull(5) ? null : reader.GetString(5),
                SortOrder = reader.GetInt32(6),
                Description = reader.IsDBNull(7) ? null : reader.GetString(7),
            };
            classMap[classId.Value].Fields.Add(field);
        }
    }

    return classes;
}

private string GetConnectionString(XafApplication application)
{
    // XAF stores the connection string in the application's ConnectionString property
    // or you can read it from configuration
    return application.ConnectionString;
}
```

### Why the Order Matters

1. **Schema sync before Roslyn** -- The table must exist before EF Core tries to query it. If you compile first and EF Core tries to validate the model, it will fail.
2. **Set `RuntimeEntityTypes` before any DbContext** -- `OnModelCreating` runs during the first `DbContext` construction. If the types are not set yet, the dynamic entities will be missing from the model.
3. **Register in TypesInfo before XAF builds views** -- XAF generates ListViews and DetailViews from TypesInfo during `Setup`. If you register types after this point, the views will not be generated for the current app session.

### Degraded Mode

The `try/catch` around `LoadRuntimeEntities` is intentional. If compilation fails (bad metadata, missing references), the application should still start. The user can fix the metadata through the Schema Management UI and trigger a hot-reload. Crashing the entire app because one runtime class has a typo would be unacceptable.

---

## Phase 5: Hot-Loading Without Restart

This is the advanced part. When a user creates a new class or adds a field, the system should make it available without an app restart. This requires careful orchestration.

### The 7-Step Hot-Load Sequence

```
1. Acquire SemaphoreSlim (prevent concurrent reloads)
2. Schema sync (CREATE/ALTER TABLE for the new/changed class)
3. Roslyn-compile all Runtime classes into a new assembly
4. Drain active ObjectSpaces (wait for in-flight saves to complete)
5. Unload old AssemblyLoadContext
6. Set new RuntimeEntityTypes, rebuild EF Core IModel
7. Refresh TypesInfo + push SignalR notification to Blazor clients
```

### Step 1: SchemaChangeOrchestrator

File: `Module/Services/SchemaChangeOrchestrator.cs`

```csharp
public class SchemaChangeOrchestrator
{
    private static readonly SemaphoreSlim _reloadLock = new(1, 1);
    private static AssemblyLoadContext _currentAlc;

    private readonly string _connectionString;

    public SchemaChangeOrchestrator(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task ReloadAsync()
    {
        if (!await _reloadLock.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("Another schema reload is already in progress.");
        }

        try
        {
            // 1. Query current metadata
            var classes = QueryCustomClasses(_connectionString); // Same as startup

            // 2. Schema sync
            var schemaSyncer = new SchemaSynchronizer(_connectionString);
            foreach (var c in classes)
            {
                schemaSyncer.SynchronizeClass(c);
            }

            // 3. Roslyn compile
            var builder = new RuntimeAssemblyBuilder();
            var assembly = builder.CompileToAssembly(classes);

            // 4. Get types from the new assembly
            var newTypes = assembly?.GetExportedTypes() ?? Array.Empty<Type>();

            // 5. Unload old ALC
            var oldAlc = _currentAlc;

            // 6. Update the static type list
            XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes = newTypes;

            // 7. Store reference to new ALC for future unload
            if (assembly != null)
            {
                _currentAlc = AssemblyLoadContext.GetLoadContext(assembly);
            }

            // 8. Register new types in TypesInfo
            foreach (var type in newTypes)
            {
                XafTypesInfo.Instance.RegisterEntity(type);
            }

            // 9. Schedule unload of old ALC (after GC collects references)
            if (oldAlc != null)
            {
                oldAlc.Unload();
                // The actual unload happens when GC collects all references
                // to types from the old assembly.
            }
        }
        finally
        {
            _reloadLock.Release();
        }
    }
}
```

**The `SemaphoreSlim` guard** prevents two simultaneous reloads. If a user rapidly creates two classes, the second reload waits for the first to complete. The 30-second timeout prevents deadlocks.

### Step 2: Hooking into ObjectSpace.Committed

Trigger a reload whenever a `CustomClass` or `CustomField` is saved:

```csharp
// In Module.cs Setup(XafApplication)
application.ObjectSpaceCreated += (sender, e) =>
{
    if (e.ObjectSpace is not IObjectSpace os)
        return;

    os.Committed += async (s, args) =>
    {
        // Check if any CustomClass or CustomField was modified
        var modifiedTypes = os.ModifiedObjects
            .Cast<object>()
            .Select(o => o.GetType())
            .ToHashSet();

        if (modifiedTypes.Contains(typeof(CustomClass)) ||
            modifiedTypes.Contains(typeof(CustomField)))
        {
            var orchestrator = new SchemaChangeOrchestrator(connectionString);
            await orchestrator.ReloadAsync();

            // Notify clients (see Step 3)
        }
    };
};
```

### Step 3: SignalR Hub for Blazor Client Notification

After a hot-reload, existing Blazor circuits have stale navigation menus. You need to tell them to refresh.

Create a SignalR hub:

```csharp
using Microsoft.AspNetCore.SignalR;

public class SchemaReloadHub : Hub
{
    public const string Url = "/schemaReloadHub";
}
```

Register it in `Startup.cs`:

```csharp
// In ConfigureServices:
services.AddSignalR();

// In Configure, inside UseEndpoints:
endpoints.MapHub<SchemaReloadHub>(SchemaReloadHub.Url);
```

After a reload, broadcast to all clients:

```csharp
// In the orchestrator or the committed handler:
var hubContext = serviceProvider.GetRequiredService<IHubContext<SchemaReloadHub>>();
await hubContext.Clients.All.SendAsync("SchemaReloaded");
```

### Step 4: Client-Side Reconnection

On the Blazor client side, listen for the SignalR message and force a page reload. The simplest approach for XAF Blazor is a full page reload -- XAF caches view metadata extensively, and a partial refresh is not practical.

Add JavaScript to your `_Host.cshtml` or a Blazor component:

```javascript
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/schemaReloadHub")
    .build();

connection.on("SchemaReloaded", function () {
    // Full page reload -- XAF will reinitialize with the new types
    location.reload();
});

connection.start();
```

### Important Caveats for Hot-Loading

- **EF Core model immutability** -- Once an EF Core `IModel` is built, it cannot be modified. The only way to add new entity types is to force a new `DbContext` construction, which triggers a fresh `OnModelCreating` call. The `RuntimeEntityTypes` static property pattern handles this because each new `DbContext` instance will pick up the latest types.
- **TypesInfo is not designed for removal** -- You can add types to XAF's TypesInfo, but removing them is unsupported. This means once a type is registered, it stays until the process restarts. For most scenarios this is fine -- users create entities more often than they delete them.
- **Active sessions** -- Users with open DetailViews for a runtime entity will see errors if that entity's type changes. The full page reload via SignalR is the safest approach.

---

## Phase 6: Entity Relationships

Runtime entities can reference other entities -- both compiled (like a `Company` class in your codebase) and other runtime entities.

### How It Works

When a `CustomField` has `ReferencedClassName` set (e.g., "Company" or "EmployeeInformation"), the code generator produces two properties instead of one:

```csharp
// For a field with FieldName = "Company" and ReferencedClassName = "Company"
public virtual Guid? CompanyId { get; set; }  // FK property
public virtual YourApp.DynamicEntities.Company Company { get; set; }  // Navigation property
```

### FK Property Pattern

The FK property is always `Guid?` (nullable). This matches the `BaseObject.Id` type (which is `Guid`). The naming convention is `{FieldName}Id` for the FK and `{FieldName}` for the navigation.

### Cross-Reference Between Runtime Entities

Because all runtime classes are compiled into a single Roslyn compilation unit, they can reference each other. If `Order` has a field referencing `Customer`, both classes exist in the same assembly and the navigation property resolves correctly.

### Referencing Compiled Entities

To reference a compiled entity (e.g., a `Company` class in your main module), you need to:

1. Add the assembly containing `Company` to the Roslyn `MetadataReferences`
2. Use the fully qualified type name in the generated code
3. Add the FK column in `SchemaSynchronizer` pointing to the compiled entity's table

```csharp
// In the source generator, for cross-assembly references:
if (IsCompiledEntity(field.ReferencedClassName))
{
    string fullTypeName = GetCompiledEntityFullName(field.ReferencedClassName);
    sb.AppendLine($"    public virtual Guid? {field.FieldName}Id {{ get; set; }}");
    sb.AppendLine($"    public virtual {fullTypeName} {field.FieldName} {{ get; set; }}");
}
```

### Limitations

- **No inverse navigation on compiled entities** -- If a runtime `EmployeeInformation` references a compiled `Company`, you cannot add a `Company.EmployeeInformations` collection property to `Company` without recompiling it. The FK works, queries work, but the navigation is one-directional.
- **Circular references between runtime entities** -- These work because all classes are in the same compilation unit. But be careful with circular required references, which can create insertion order problems.

### SchemaSynchronizer for FK Columns

When creating the FK column, also create the FK constraint:

```csharp
// In SchemaSynchronizer, after creating or altering the table:
if (!string.IsNullOrWhiteSpace(field.ReferencedClassName))
{
    string fkColumn = $"{field.FieldName}Id";
    string referencedTable = field.ReferencedClassName;

    // Check if constraint already exists
    string constraintName = $"FK_{customClass.ClassName}_{fkColumn}";
    if (!ConstraintExists(conn, constraintName))
    {
        string sql = $"ALTER TABLE \"{customClass.ClassName}\" " +
                     $"ADD CONSTRAINT \"{constraintName}\" " +
                     $"FOREIGN KEY (\"{fkColumn}\") " +
                     $"REFERENCES \"{referencedTable}\" (\"Id\")";
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }
}
```

---

## Phase 7: Graduation

"Graduation" is the process of converting a runtime entity into a regular compiled class in your codebase. Once graduated, the class is no longer managed by Roslyn -- it is a normal C# file checked into source control.

### Why Graduate?

- **Performance** -- Runtime entities go through an extra indirection layer. Compiled entities are direct.
- **Custom logic** -- You cannot add business rules, validation, or method overrides to a Roslyn-generated class. Once it is compiled code, you can.
- **Source control** -- A compiled class is versioned, reviewed, and tested like any other code.

### The Graduation Process

```
1. Set Status = Graduating (prevents hot-load from recompiling it)
2. Export the generated C# source code to a .cs file
3. Export the DbContext snippet (the modelBuilder.Entity<T> configuration)
4. Export a migration note (what table already exists, no data migration needed)
5. Developer adds the .cs file to the project, updates DbContext, rebuilds
6. Set Status = Compiled
```

### Export the Generated Source

The `RuntimeAssemblyBuilder.GenerateClassSource()` method already produces valid C# code. To graduate, you simply write that output to a file and adjust the namespace:

```csharp
public string ExportForGraduation(CustomClass customClass)
{
    // Generate the source as usual
    string source = GenerateClassSource(customClass);

    // Replace the dynamic namespace with the project's real namespace
    source = source.Replace(
        "namespace YourApp.DynamicEntities",
        "namespace YourApp.Module.BusinessObjects");

    return source;
}
```

### Export the DbContext Snippet

```csharp
public string ExportDbContextSnippet(CustomClass customClass)
{
    var sb = new StringBuilder();
    sb.AppendLine($"// Add to DbContext: public DbSet<{customClass.ClassName}> {customClass.ClassName}s {{ get; set; }}");
    sb.AppendLine();
    sb.AppendLine($"// Add to OnModelCreating:");
    sb.AppendLine($"modelBuilder.Entity<{customClass.ClassName}>(entity =>");
    sb.AppendLine("{");
    sb.AppendLine($"    entity.ToTable(\"{customClass.ClassName}\");");

    foreach (var field in customClass.Fields.Where(f => f.IsRequired))
    {
        sb.AppendLine($"    entity.Property(e => e.{field.FieldName}).IsRequired();");
    }

    sb.AppendLine("});");

    return sb.ToString();
}
```

### Zero Data Migration

The key insight is that the PostgreSQL table already exists with data in it. The compiled class maps to the same table. You do not need an EF Core migration to create the table -- it is already there. Your migration note should say:

```
Table "{ClassName}" already exists and contains data.
Do NOT generate an EF Core migration for this table.
Add an empty migration with .HasData() only if you need seed data.
```

---

## Phase 8: Common Pitfalls

Things that will trip you up, roughly in order of how often they bite.

### 1. XAF TypesInfo is Not Designed for Runtime Mutation

`XafTypesInfo.Instance.RegisterEntity(type)` works for adding types, but there is no `UnregisterEntity`. Once a type is registered, it stays for the lifetime of the process. If you recompile and the new assembly has a different version of the same type, you will have two `TypeInfo` entries for the same class name. XAF does not handle this gracefully.

**Workaround:** Always use unique assembly names (timestamp-based) so the old and new types are technically different CLR types. XAF will use the most recently registered one. The old type's views become orphaned but harmless.

### 2. EF Core Model is Immutable Once Built

After `OnModelCreating` runs and the `IModel` is created, you cannot modify it. Adding a new runtime entity requires a new `DbContext` construction, which means a new `IModel`. The `RuntimeEntityTypes` static property pattern ensures that the next `DbContext` to be created picks up the new types.

**The trap:** If you cache a `DbContext` instance (e.g., in a singleton service), it will never see new types. XAF creates `DbContext` instances per-request via its `ObjectSpace` pattern, which naturally works with this approach.

### 3. Npgsql Timestamp Behavior

Npgsql 6+ uses `timestamptz` by default and rejects `DateTime` values that do not have `DateTimeKind.Utc`. XAF uses `DateTime` (not `DateTimeOffset`) throughout its base classes.

```csharp
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
```

Without this switch, you will get runtime exceptions the first time XAF tries to read or write a `DateTime` column. The switch must be set before any `NpgsqlConnection` is opened.

### 4. Collectible ALC Restrictions

Types loaded from a collectible ALC have restrictions:
- They cannot be used in `typeof()` expressions at compile time (obviously).
- Reflection works, but cached `MethodInfo` / `PropertyInfo` objects become invalid after the ALC is unloaded.
- Static fields on collectible types are lost when the ALC unloads.
- Thread-static and async-local values referencing collectible types can prevent unloading.

**In practice:** XAF uses reflection heavily (TypesInfo, model generation). After an ALC unload/reload cycle, some XAF caches may hold stale type references. The full page reload via SignalR clears these.

### 5. DevExpress UseConnectionString Defaults to SQL Server

If you use the XAF template's `UseConnectionString` helper, it silently assumes SQL Server:

```csharp
// THIS ASSUMES SQL SERVER:
options.UseConnectionString(connectionString);

// USE THIS FOR POSTGRESQL:
options.UseNpgsql(connectionString);
```

The symptom is a cryptic `SqlException` about failed login or invalid server name, even though your connection string is a valid PostgreSQL connection string.

### 6. XAF Blazor DOM Quirks

If you write Playwright tests (recommended), be aware of these XAF Blazor UI patterns:

- **Click area overlays** -- Navigation links have a `.xaf-navigation-link-click-area` overlay with `pointer-events: auto` that sits on top of the actual link. Standard Playwright clicks may miss. Use `force=True`.
- **Custom elements** -- Toolbar buttons are `<dxbl-toolbar-item>` custom elements, not standard `<button>`. Use attribute selectors: `dxbl-toolbar-item[text="New"]`.
- **Form field identification** -- XAF Blazor uses `data-item-name` attributes on hidden `<div>` elements within `.dxbl-fl-ctrl` containers. The actual `<input>` is a sibling, not a child of the labeled element.
- **Accordion navigation** -- Navigation groups use `<dxbl-group-control>` with an `expanded` class. Check expansion state before clicking a child item.

### 7. PostgreSQL Table/Column Name Casing

PostgreSQL folds unquoted identifiers to lowercase. EF Core generates quoted identifiers (`"ClassName"`, not `classname`). Your DDL in `SchemaSynchronizer` must also quote identifiers, or you will end up with two columns: `"ClassName"` and `classname`.

Always use `"double quotes"` around table and column names in your DDL.

### 8. BaseObject.Id Generation

`BaseObject` uses `Guid` for its `Id` property. By default, EF Core will expect the database to generate it. In PostgreSQL, use `gen_random_uuid()` as the default. Do not use `Guid.NewGuid()` in the constructor -- let the database handle it for consistency with XAF's behavior.

---

## Phase 9: Testing Strategy

Automated testing of XAF Blazor applications requires Playwright and some XAF-specific knowledge.

### Setup

Use Playwright with Python (or Node.js). The project uses a Docker container with Playwright pre-installed:

```dockerfile
FROM mcr.microsoft.com/playwright/python:v1.48.0-noble
WORKDIR /workspace
COPY tests/requirements.txt .
RUN pip install -r requirements.txt
```

```txt
# requirements.txt
playwright==1.48.0
pytest==8.3.3
pytest-html==4.1.1
```

### Pytest Fixtures

```python
import pytest
from playwright.sync_api import sync_playwright

BASE_URL = os.environ.get("BASE_URL", "https://host.docker.internal:5001")

@pytest.fixture(scope="session")
def browser():
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        yield browser
        browser.close()

@pytest.fixture(scope="function")
def page(browser):
    ctx = browser.new_context(
        viewport={"width": 1920, "height": 1080},
        ignore_https_errors=True,  # XAF dev cert
    )
    ctx.set_default_timeout(30000)
    pg = ctx.new_page()
    pg.goto(BASE_URL, wait_until="networkidle", timeout=30000)
    pg.wait_for_selector(".xaf-nav-link", timeout=30000)  # Wait for XAF to load
    yield pg
    pg.close()
    ctx.close()
```

### XAF-Specific Selectors

Here is a cheat sheet of selectors for common XAF Blazor UI elements:

| Element | Selector |
|---------|----------|
| Navigation group | `.xaf-nav-link:has-text('Group Name')` |
| Navigation item (within accordion) | `.dxbl-accordion-item .xaf-nav-link:has-text('Item')` |
| Grid (ListView) | `.dxbl-grid` |
| Grid rows | `.dxbl-grid-table tbody tr[data-visible-index]` |
| Toolbar New button | `dxbl-toolbar-item[text="New"]` |
| Toolbar Save button | `dxbl-toolbar-item[text="Save"]` |
| Toolbar Delete button | `dxbl-toolbar-item[text="Delete"]` |
| Delete confirmation (Yes) | `.dxbl-popup-footer button:has-text('Yes')` |
| Form field container | `.dxbl-fl-ctrl:has([data-item-name='FieldLabel'])` |
| Form field input | `.dxbl-fl-ctrl:has([data-item-name='FieldLabel']) input:not([type='hidden'])` |
| Form field textarea | `.dxbl-fl-ctrl:has([data-item-name='FieldLabel']) textarea` |

### Page Object Pattern

Organize your tests with page objects. Here is the recommended structure:

```
tests/
  conftest.py              # Fixtures: browser, context, page
  pages/
    base_page.py           # click_new(), click_save(), click_delete(), wait_for_loading()
    navigation_page.py     # navigate_to(group, item)
    list_view_page.py      # wait_for_grid(), find_row_with_text(), select_row_with_text()
    detail_view_page.py    # fill_field(label, value), get_field_value(label)
  tests/
    test_metadata_crud.py  # CRUD for CustomClass and CustomField
    test_runtime_entity.py # Create class via UI, verify table and nav item appear
```

### Force Clicks on Navigation

XAF Blazor's navigation pane has invisible overlay elements that intercept clicks. Always use `force=True`:

```python
def navigate_to(self, group, item):
    # Must force-click to bypass .xaf-navigation-link-click-area overlay
    self.page.locator(f".xaf-nav-link:has-text('{group}')").first.click(force=True)
    self.page.wait_for_timeout(1000)

    self.page.locator(
        f".dxbl-accordion-item .xaf-nav-link:has-text('{item}')"
    ).first.click(force=True)
    self.page.wait_for_load_state("networkidle")
```

### Form Field Identification

XAF Blazor forms use a non-obvious DOM structure. Each form field is wrapped in a `.dxbl-fl-ctrl` container that contains a hidden `<div data-item-name="FieldLabel">`. The actual input is a sibling element:

```python
def fill_field(self, label, value):
    """Fill a field by its XAF data-item-name label."""
    selector = f".dxbl-fl-ctrl:has([data-item-name='{label}']) input:not([type='hidden'])"
    field = self.page.locator(selector).first
    field.click()
    field.fill(value)
    field.press("Tab")  # Trigger XAF's change detection
```

The `Tab` press after filling is important -- XAF Blazor only processes input changes on blur, not on every keystroke.

### Test Execution

```bash
# Start PostgreSQL
docker compose up -d postgres

# Run the XAF app (in another terminal)
dotnet run --project YourApp.Blazor.Server

# Run tests
docker compose build python
docker compose up -d python
docker exec your-python-container pytest /workspace/tests/ -v
```

---

## Summary: File Inventory

When you are done implementing all phases, your project should have these new files:

```
Module/
  BusinessObjects/
    CustomClass.cs           # Phase 1 -- metadata entity
    CustomField.cs           # Phase 1 -- metadata entity
  Services/
    SupportedTypes.cs        # Phase 1 -- CLR-to-PostgreSQL type map
    RuntimeAssemblyBuilder.cs # Phase 2 -- Roslyn source gen + compilation
    CollectibleLoadContext.cs  # Phase 2 -- collectible ALC
    SchemaSynchronizer.cs     # Phase 3 -- DDL execution
    SchemaChangeOrchestrator.cs # Phase 5 -- hot-load orchestration

Blazor.Server/
  Hubs/
    SchemaReloadHub.cs       # Phase 5 -- SignalR for client notification
```

Modified files:

```
Module/
  BusinessObjects/YourAppDbContext.cs  # Phase 1 -- DbSets, RuntimeEntityTypes, OnModelCreating
  Module.cs                            # Phase 1 -- AdditionalExportedTypes; Phase 4 -- Setup wiring

Blazor.Server/
  Startup.cs                           # Phase 1 -- UseNpgsql; Phase 5 -- SignalR registration
```

Build incrementally. Get Phase 1 working and verified through the UI before moving to Phase 2. Each phase builds on the previous one, and debugging is much easier when you know the foundation is solid.
