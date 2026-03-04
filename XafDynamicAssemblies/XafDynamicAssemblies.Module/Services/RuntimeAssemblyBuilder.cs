using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XafDynamicAssemblies.Module.BusinessObjects;

namespace XafDynamicAssemblies.Module.Services
{
    public class CompilationResult
    {
        public Assembly Assembly { get; set; }
        public AssemblyLoadContext LoadContext { get; set; }
        public Type[] RuntimeTypes { get; set; } = Array.Empty<Type>();
        public Dictionary<string, string> GeneratedSources { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public bool Success => Errors.Count == 0;
    }

    public static class RuntimeAssemblyBuilder
    {
        private const string RuntimeNamespace = "XafDynamicAssemblies.RuntimeEntities";

        /// <summary>
        /// Validates compilation without loading into an ALC. Returns diagnostics only.
        /// </summary>
        public static CompilationResult ValidateCompilation(List<CustomClass> classes)
        {
            var result = new CompilationResult();

            if (classes.Count == 0)
                return result;

            var syntaxTrees = new List<SyntaxTree>();
            foreach (var cc in classes)
            {
                var source = GenerateSource(cc);
                result.GeneratedSources[cc.ClassName] = source;
                syntaxTrees.Add(CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp12)));
            }

            var references = GetMetadataReferences();
            var compilation = CSharpCompilation.Create(
                assemblyName: $"ValidateOnly_{Guid.NewGuid():N}",
                syntaxTrees: syntaxTrees,
                references: references,
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release
                )
            );

            using var ms = new MemoryStream();
            var emitResult = compilation.Emit(ms);

            foreach (var diag in emitResult.Diagnostics)
            {
                if (diag.Severity == DiagnosticSeverity.Error)
                    result.Errors.Add(diag.ToString());
                else if (diag.Severity == DiagnosticSeverity.Warning)
                    result.Warnings.Add(diag.ToString());
            }

            return result;
        }

        /// <summary>
        /// Compiles all CustomClass metadata into a single assembly loaded in a collectible ALC.
        /// </summary>
        public static CompilationResult Compile(List<CustomClass> classes)
        {
            var result = new CompilationResult();

            if (classes.Count == 0)
            {
                result.RuntimeTypes = Array.Empty<Type>();
                return result;
            }

            // Generate source for each class
            var syntaxTrees = new List<SyntaxTree>();
            foreach (var cc in classes)
            {
                var source = GenerateSource(cc);
                result.GeneratedSources[cc.ClassName] = source;
                syntaxTrees.Add(CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp12)));
            }

            // Collect metadata references
            var references = GetMetadataReferences();

            // Compile
            var compilation = CSharpCompilation.Create(
                assemblyName: $"RuntimeEntities_{Guid.NewGuid():N}",
                syntaxTrees: syntaxTrees,
                references: references,
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release,
                    allowUnsafe: false
                )
            );

            using var ms = new MemoryStream();
            var emitResult = compilation.Emit(ms);

            // Collect diagnostics
            foreach (var diag in emitResult.Diagnostics)
            {
                if (diag.Severity == DiagnosticSeverity.Error)
                    result.Errors.Add(diag.ToString());
                else if (diag.Severity == DiagnosticSeverity.Warning)
                    result.Warnings.Add(diag.ToString());
            }

            if (!emitResult.Success)
                return result;

            // Load into collectible ALC
            ms.Seek(0, SeekOrigin.Begin);
            var alc = new CollectibleLoadContext();
            var assembly = alc.LoadFromStream(ms);

            result.Assembly = assembly;
            result.LoadContext = alc;
            result.RuntimeTypes = assembly.GetExportedTypes();

            return result;
        }

        /// <summary>
        /// Generates C# source code for a single CustomClass.
        /// </summary>
        public static string GenerateSource(CustomClass cc)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.ComponentModel;");
            sb.AppendLine("using System.ComponentModel.DataAnnotations.Schema;");
            sb.AppendLine("using DevExpress.ExpressApp;");
            sb.AppendLine("using DevExpress.ExpressApp.DC;");
            sb.AppendLine("using DevExpress.Persistent.Base;");
            sb.AppendLine("using DevExpress.Persistent.BaseImpl.EF;");
            sb.AppendLine("using XafDynamicAssemblies.Module.BusinessObjects;");
            sb.AppendLine();
            sb.AppendLine($"namespace {RuntimeNamespace}");
            sb.AppendLine("{");

            // Class attributes
            sb.AppendLine("    [DefaultClassOptions]");
            if (!string.IsNullOrWhiteSpace(cc.NavigationGroup))
                sb.AppendLine($"    [NavigationItem(\"{EscapeString(cc.NavigationGroup)}\")]");

            // Find the default property (first IsDefaultField, or first string field, or first field)
            var defaultField = FindDefaultProperty(cc);
            if (defaultField != null)
                sb.AppendLine($"    [DefaultProperty(\"{defaultField.FieldName}\")]");

            sb.AppendLine($"    public class {cc.ClassName} : BaseObject");
            sb.AppendLine("    {");

            // Generate properties
            var fields = cc.Fields
                .Where(f => !string.IsNullOrWhiteSpace(f.FieldName))
                .OrderBy(f => f.SortOrder)
                .ThenBy(f => f.FieldName);

            foreach (var field in fields)
            {
                if (IsReferenceField(field))
                {
                    // Reference field: generate FK property + navigation property
                    var refTypeName = field.ReferencedClassName;
                    var fkPropName = field.FieldName + "Id";
                    sb.AppendLine($"        public virtual Guid? {fkPropName} {{ get; set; }}");
                    sb.AppendLine($"        [ForeignKey(\"{fkPropName}\")]");
                    sb.AppendLine($"        public virtual {refTypeName} {field.FieldName} {{ get; set; }}");
                }
                else
                {
                    var clrType = MapToClrTypeName(field.TypeName);
                    var nullable = IsNullableType(field.TypeName) && !field.IsRequired ? "?" : "";

                    // Value types need nullable suffix when not required
                    if (!field.IsRequired && IsValueType(field.TypeName))
                        nullable = "?";
                    else if (!field.IsRequired && !IsValueType(field.TypeName))
                        nullable = "";

                    sb.AppendLine($"        public virtual {clrType}{nullable} {field.FieldName} {{ get; set; }}");
                }
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static CustomField FindDefaultProperty(CustomClass cc)
        {
            // Prefer explicit IsDefaultField
            var defaultField = cc.Fields.FirstOrDefault(f => f.IsDefaultField);
            if (defaultField != null) return defaultField;

            // Prefer first string field
            defaultField = cc.Fields
                .Where(f => f.TypeName == "System.String" && !string.IsNullOrWhiteSpace(f.FieldName))
                .OrderBy(f => f.SortOrder)
                .FirstOrDefault();
            if (defaultField != null) return defaultField;

            // First field
            return cc.Fields
                .Where(f => !string.IsNullOrWhiteSpace(f.FieldName))
                .OrderBy(f => f.SortOrder)
                .FirstOrDefault();
        }

        private static bool IsReferenceField(CustomField field)
        {
            return !string.IsNullOrWhiteSpace(field.ReferencedClassName)
                && (field.TypeName == "Reference" || string.IsNullOrWhiteSpace(field.TypeName));
        }

        private static string MapToClrTypeName(string typeName)
        {
            return typeName switch
            {
                "System.String" => "string",
                "System.Int32" => "int",
                "System.Int64" => "long",
                "System.Decimal" => "decimal",
                "System.Double" => "double",
                "System.Single" => "float",
                "System.Boolean" => "bool",
                "System.DateTime" => "DateTime",
                "System.Guid" => "Guid",
                "System.Byte[]" => "byte[]",
                _ => typeName
            };
        }

        private static bool IsValueType(string typeName)
        {
            return typeName is "System.Int32" or "System.Int64" or "System.Decimal"
                or "System.Double" or "System.Single" or "System.Boolean"
                or "System.DateTime" or "System.Guid";
        }

        private static bool IsNullableType(string typeName)
        {
            return IsValueType(typeName);
        }

        private static string EscapeString(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static List<MetadataReference> GetMetadataReferences()
        {
            var references = new List<MetadataReference>();

            // Use TRUSTED_PLATFORM_ASSEMBLIES to find runtime assemblies
            var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")?.ToString();
            if (trustedAssemblies != null)
            {
                foreach (var path in trustedAssemblies.Split(Path.PathSeparator))
                {
                    if (File.Exists(path))
                    {
                        try
                        {
                            references.Add(MetadataReference.CreateFromFile(path));
                        }
                        catch
                        {
                            // Skip assemblies that can't be loaded as metadata references
                        }
                    }
                }
            }

            // Also add from currently loaded assemblies (catches DevExpress assemblies)
            var loadedPaths = new HashSet<string>(references
                .OfType<PortableExecutableReference>()
                .Select(r => r.FilePath ?? ""),
                StringComparer.OrdinalIgnoreCase);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                try
                {
                    var loc = asm.Location;
                    if (!string.IsNullOrEmpty(loc) && File.Exists(loc) && !loadedPaths.Contains(loc))
                    {
                        references.Add(MetadataReference.CreateFromFile(loc));
                        loadedPaths.Add(loc);
                    }
                }
                catch
                {
                    // Skip
                }
            }

            return references;
        }
    }

    /// <summary>
    /// Custom AssemblyLoadContext for runtime-compiled assemblies.
    /// Non-collectible by default to allow EF Core proxy generation.
    /// Phase 4 (hot-load) will introduce a collectible variant with proxy workarounds.
    /// </summary>
    public class CollectibleLoadContext : AssemblyLoadContext
    {
        public CollectibleLoadContext() : base(isCollectible: false) { }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            // Return null to fall back to the default context
            return null;
        }
    }
}
