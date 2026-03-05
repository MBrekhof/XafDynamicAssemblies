using System.Text;
using XafDynamicAssemblies.Module.BusinessObjects;

namespace XafDynamicAssemblies.Module.Services
{
    /// <summary>
    /// Generates production C# source code, DbContext snippet, and migration note
    /// for graduating a runtime entity to compiled code.
    /// </summary>
    public static class GraduationService
    {
        /// <summary>
        /// Generate the full graduation artifact: entity class source + DbContext snippet + migration note.
        /// </summary>
        public static string GenerateGraduationSource(CustomClass cc)
        {
            var sb = new StringBuilder();

            sb.AppendLine("// ============================================================");
            sb.AppendLine($"// Graduated Entity: {cc.ClassName}");
            sb.AppendLine($"// Generated from runtime entity on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("// ============================================================");
            sb.AppendLine();

            // Section 1: Entity class
            sb.AppendLine("// --- Entity Class ---");
            sb.AppendLine();
            sb.Append(GenerateEntityClass(cc));
            sb.AppendLine();

            // Section 2: DbContext registration
            sb.AppendLine("// --- DbContext Registration ---");
            sb.AppendLine("// Add this DbSet property to your DbContext:");
            sb.AppendLine($"//   public DbSet<{cc.ClassName}> {cc.ClassName}s => Set<{cc.ClassName}>();");
            sb.AppendLine("//");
            sb.AppendLine("// Add this to OnModelCreating:");
            sb.AppendLine($"//   modelBuilder.Entity<{cc.ClassName}>().ToTable(\"{cc.ClassName}\");");
            sb.AppendLine();

            // Section 3: Migration note
            sb.AppendLine("// --- Migration Note ---");
            sb.AppendLine($"// The table \"{cc.ClassName}\" already exists in the database.");
            sb.AppendLine("// Do NOT create an EF Core migration for this entity.");
            sb.AppendLine("// Simply register the entity in your DbContext and it will use the existing table.");
            sb.AppendLine("// If you need to add new columns later, create a migration as usual.");

            // Section 4: Web API note (if applicable)
            if (cc.IsApiExposed)
            {
                sb.AppendLine();
                sb.AppendLine("// --- Web API Note ---");
                sb.AppendLine("// This entity was API-exposed at runtime.");
                sb.AppendLine($"// Add options.BusinessObject<{cc.ClassName}>() to AddWebApi in Startup.cs.");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Generate a proper C# entity class (not the Roslyn-compiled version, but a formatted one
        /// suitable for inclusion in a compiled project).
        /// </summary>
        private static string GenerateEntityClass(CustomClass cc)
        {
            var sb = new StringBuilder();

            sb.AppendLine("using System;");
            sb.AppendLine("using System.ComponentModel;");
            sb.AppendLine("using System.ComponentModel.DataAnnotations.Schema;");
            sb.AppendLine("using DevExpress.ExpressApp;");
            sb.AppendLine("using DevExpress.Persistent.Base;");
            sb.AppendLine("using DevExpress.Persistent.BaseImpl.EF;");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(cc.Description))
            {
                sb.AppendLine("/// <summary>");
                sb.AppendLine($"/// {EscapeXmlComment(cc.Description)}");
                sb.AppendLine("/// </summary>");
            }

            if (!cc.GenerateAsPartial)
            {
                sb.AppendLine("[DefaultClassOptions]");
                if (!string.IsNullOrWhiteSpace(cc.NavigationGroup))
                    sb.AppendLine($"[NavigationItem(\"{EscapeString(cc.NavigationGroup)}\")]");

                var defaultField = cc.Fields.FirstOrDefault(f => f.IsDefaultField)
                    ?? cc.Fields.FirstOrDefault(f => f.TypeName == "System.String");
                if (defaultField != null)
                    sb.AppendLine($"[DefaultProperty(\"{defaultField.FieldName}\")]");
            }

            var partial = cc.GenerateAsPartial ? "partial " : "";
            sb.AppendLine($"public {partial}class {cc.ClassName} : BaseObject");
            sb.AppendLine("{");

            var fields = cc.Fields
                .Where(f => !string.IsNullOrWhiteSpace(f.FieldName))
                .OrderBy(f => f.SortOrder)
                .ThenBy(f => f.FieldName);

            foreach (var field in fields)
            {
                if (!string.IsNullOrWhiteSpace(field.Description))
                {
                    sb.AppendLine($"    /// <summary>{EscapeXmlComment(field.Description)}</summary>");
                }

                EmitFieldAttributes(sb, field);

                if (IsReferenceField(field))
                {
                    var fkPropName = field.FieldName + "Id";
                    var guidNullable = field.IsRequired ? "" : "?";
                    if (field.IsRequired)
                        sb.AppendLine($"    [System.ComponentModel.DataAnnotations.Required]");
                    sb.AppendLine($"    public virtual Guid{guidNullable} {fkPropName} {{ get; set; }}");
                    sb.AppendLine($"    [ForeignKey(\"{fkPropName}\")]");
                    sb.AppendLine($"    public virtual {field.ReferencedClassName} {field.FieldName} {{ get; set; }}");
                }
                else
                {
                    var clrType = MapToClrTypeName(field.TypeName);
                    var nullable = !field.IsRequired && IsValueType(field.TypeName) ? "?" : "";
                    if (field.IsRequired)
                        sb.AppendLine($"    [System.ComponentModel.DataAnnotations.Required]");

                    if (field.TypeName == "System.String" && field.StringMaxLength.HasValue)
                        sb.AppendLine($"    [DevExpress.Persistent.Base.Size({field.StringMaxLength.Value})]");

                    sb.AppendLine($"    public virtual {clrType}{nullable} {field.FieldName} {{ get; set; }}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void EmitFieldAttributes(StringBuilder sb, CustomField field)
        {
            if (field.IsImmediatePostData)
                sb.AppendLine("    [ImmediatePostData]");
            if (!field.IsVisibleInListView)
                sb.AppendLine("    [VisibleInListView(false)]");
            if (!field.IsVisibleInDetailView)
                sb.AppendLine("    [VisibleInDetailView(false)]");
            if (!field.IsEditable)
                sb.AppendLine("    [Editable(false)]");
            if (!string.IsNullOrWhiteSpace(field.ToolTip))
                sb.AppendLine($"    [ToolTip(\"{EscapeString(field.ToolTip)}\")]");
            if (!string.IsNullOrWhiteSpace(field.DisplayName))
                sb.AppendLine($"    [DisplayName(\"{EscapeString(field.DisplayName)}\")]");
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

        private static string EscapeString(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string EscapeXmlComment(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
