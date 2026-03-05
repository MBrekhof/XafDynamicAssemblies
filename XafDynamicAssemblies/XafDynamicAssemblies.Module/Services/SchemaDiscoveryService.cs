using System.Text;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using XafDynamicAssemblies.Module.BusinessObjects;

namespace XafDynamicAssemblies.Module.Services
{
    public class CustomClassSummary
    {
        public string ClassName { get; set; }
        public int FieldCount { get; set; }
        public CustomClassStatus Status { get; set; }
        public bool IsDeployed { get; set; }
    }

    public class SchemaInfo
    {
        public List<string> CompiledEntities { get; set; } = new();
    }

    public class SchemaDiscoveryService
    {
        private static readonly HashSet<string> MetadataTypeNames = new()
        {
            nameof(CustomClass),
            nameof(CustomField),
            nameof(SchemaHistory),
            nameof(SchemaPackage),
        };

        private readonly object _cacheLock = new();
        private SchemaInfo _cachedSchema;

        public void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _cachedSchema = null;
            }
        }

        public SchemaInfo GetSchema()
        {
            lock (_cacheLock)
            {
                if (_cachedSchema != null)
                    return _cachedSchema;

                _cachedSchema = DiscoverSchema();
                return _cachedSchema;
            }
        }

        public string GenerateSystemPrompt(List<CustomClassSummary> runtimeEntities)
        {
            var schema = GetSchema();
            var sb = new StringBuilder();

            // Role
            sb.AppendLine("You are a schema design assistant for an XAF application with runtime entity support.");
            sb.AppendLine("You help users create, modify, and manage business object types and their fields at runtime.");
            sb.AppendLine();

            // Rules
            sb.AppendLine("## Rules");
            sb.AppendLine("- Always confirm with the user before executing any schema change (create, modify, delete).");
            sb.AppendLine("- After making changes, remind the user to Deploy so changes take effect.");
            sb.AppendLine("- Infer appropriate field types from natural language descriptions (e.g., \"price\" -> System.Decimal, \"active\" -> System.Boolean).");
            sb.AppendLine("- Use PascalCase for class names and field names.");
            sb.AppendLine("- Class names must be valid C# identifiers and cannot be C# keywords or reserved type names.");
            sb.AppendLine("- Field names must be valid C# identifiers and cannot be reserved (Id, ObjectType, GCRecord, OptimisticLockField).");
            sb.AppendLine();

            // Supported field types
            sb.AppendLine("## Supported Field Types");
            foreach (var typeName in SupportedTypes.AllTypeNames)
            {
                sb.AppendLine($"- {typeName}");
            }
            sb.AppendLine();

            // Runtime entities (metadata)
            sb.AppendLine("## Runtime Entities (Metadata-Defined)");
            if (runtimeEntities.Count == 0)
            {
                sb.AppendLine("No runtime entities defined yet.");
            }
            else
            {
                foreach (var entity in runtimeEntities)
                {
                    var deployed = entity.IsDeployed ? "deployed" : "not deployed";
                    sb.AppendLine($"- **{entity.ClassName}**: {entity.FieldCount} fields, status={entity.Status}, {deployed}");
                }
            }
            sb.AppendLine();

            // Compiled entities (available for references)
            sb.AppendLine("## Compiled Entities (Available for References)");
            if (schema.CompiledEntities.Count == 0)
            {
                sb.AppendLine("No compiled entities discovered.");
            }
            else
            {
                foreach (var name in schema.CompiledEntities.OrderBy(n => n))
                {
                    sb.AppendLine($"- {name}");
                }
            }

            return sb.ToString();
        }

        private SchemaInfo DiscoverSchema()
        {
            var info = new SchemaInfo();

            var runtimeTypeNames = new HashSet<string>(
                XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes.Select(t => t.Name));

            try
            {
                foreach (var typeInfo in XafTypesInfo.Instance.PersistentTypes)
                {
                    if (typeInfo.Type == null)
                        continue;

                    var name = typeInfo.Name;
                    var ns = typeInfo.Type.Namespace ?? "";

                    // Skip DevExpress internal types
                    if (ns.StartsWith("DevExpress", StringComparison.Ordinal))
                        continue;

                    // Skip runtime entities (already tracked via CustomClass metadata)
                    if (runtimeTypeNames.Contains(name))
                        continue;

                    // Skip metadata types
                    if (MetadataTypeNames.Contains(name))
                        continue;

                    info.CompiledEntities.Add(name);
                }
            }
            catch (InvalidOperationException)
            {
                // XafTypesInfo not yet initialized — return empty
            }

            return info;
        }
    }
}
