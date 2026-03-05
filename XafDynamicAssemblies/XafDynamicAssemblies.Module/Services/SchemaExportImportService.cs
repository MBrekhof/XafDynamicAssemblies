using System.Text.Json;
using System.Text.Json.Serialization;
using DevExpress.ExpressApp;
using XafDynamicAssemblies.Module.BusinessObjects;

namespace XafDynamicAssemblies.Module.Services
{
    public static class SchemaExportImportService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static string Export(IObjectSpace objectSpace)
        {
            var classes = objectSpace.GetObjectsQuery<CustomClass>()
                .Where(c => c.Status == CustomClassStatus.Runtime)
                .OrderBy(c => c.ClassName)
                .ToList();

            var package = new SchemaPackageDto
            {
                Version = "1.0",
                ExportedAt = DateTime.UtcNow,
                Classes = classes.Select(c => new CustomClassDto
                {
                    ClassName = c.ClassName,
                    NavigationGroup = c.NavigationGroup,
                    Description = c.Description,
                    IsApiExposed = c.IsApiExposed,
                    Fields = c.Fields
                        .Where(f => !f.IsDefaultField)
                        .OrderBy(f => f.SortOrder)
                        .ThenBy(f => f.FieldName)
                        .Select(f => new CustomFieldDto
                        {
                            FieldName = f.FieldName,
                            TypeName = f.TypeName,
                            IsRequired = f.IsRequired,
                            Description = f.Description,
                            ReferencedClassName = f.ReferencedClassName,
                            SortOrder = f.SortOrder,
                            IsImmediatePostData = f.IsImmediatePostData,
                            StringMaxLength = f.StringMaxLength,
                            IsVisibleInListView = f.IsVisibleInListView,
                            IsVisibleInDetailView = f.IsVisibleInDetailView,
                            IsEditable = f.IsEditable,
                            ToolTip = f.ToolTip,
                            DisplayName = f.DisplayName,
                        }).ToList(),
                }).ToList(),
            };

            return JsonSerializer.Serialize(package, JsonOptions);
        }

        public static SchemaImportResult Import(IObjectSpace objectSpace, string json)
        {
            SchemaPackageDto package;
            try
            {
                package = JsonSerializer.Deserialize<SchemaPackageDto>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                return new SchemaImportResult(false, $"Invalid JSON: {ex.Message}");
            }

            if (package?.Classes is not { Count: > 0 })
                return new SchemaImportResult(false, "No classes found in the schema package.");

            int created = 0, updated = 0;
            var changes = new List<string>();

            foreach (var dto in package.Classes)
            {
                var existing = objectSpace.GetObjectsQuery<CustomClass>()
                    .FirstOrDefault(c => c.ClassName == dto.ClassName);

                if (existing is not null)
                {
                    var classChanges = UpdateClass(objectSpace, existing, dto);
                    if (classChanges.Count > 0)
                        changes.Add($"Updated '{dto.ClassName}': {string.Join(", ", classChanges)}");
                    updated++;
                }
                else
                {
                    CreateClass(objectSpace, dto);
                    changes.Add($"Created '{dto.ClassName}' with {dto.Fields.Count} field(s)");
                    created++;
                }
            }

            objectSpace.CommitChanges();

            return new SchemaImportResult(true, $"Import complete. Created: {created}, Updated: {updated}.")
            {
                Details = string.Join("\n", changes),
            };
        }

        private static void CreateClass(IObjectSpace objectSpace, CustomClassDto dto)
        {
            var cc = objectSpace.CreateObject<CustomClass>();
            cc.ClassName = dto.ClassName;
            cc.NavigationGroup = dto.NavigationGroup;
            cc.Description = dto.Description;
            cc.IsApiExposed = dto.IsApiExposed;
            cc.Status = CustomClassStatus.Runtime;

            foreach (var fieldDto in dto.Fields)
            {
                AddField(objectSpace, cc, fieldDto);
            }
        }

        private static List<string> UpdateClass(IObjectSpace objectSpace, CustomClass existing, CustomClassDto dto)
        {
            var changes = new List<string>();

            if (existing.NavigationGroup != dto.NavigationGroup)
            {
                changes.Add($"NavigationGroup: '{existing.NavigationGroup}' -> '{dto.NavigationGroup}'");
                existing.NavigationGroup = dto.NavigationGroup;
            }
            if (existing.Description != dto.Description)
            {
                changes.Add("Description changed");
                existing.Description = dto.Description;
            }
            if (existing.IsApiExposed != dto.IsApiExposed)
            {
                changes.Add($"IsApiExposed: {existing.IsApiExposed} -> {dto.IsApiExposed}");
                existing.IsApiExposed = dto.IsApiExposed;
            }

            var importFieldNames = dto.Fields.Select(f => f.FieldName).ToHashSet();

            var fieldsToRemove = existing.Fields
                .Where(f => !f.IsDefaultField && !importFieldNames.Contains(f.FieldName))
                .ToList();
            foreach (var field in fieldsToRemove)
            {
                changes.Add($"removed field '{field.FieldName}'");
                objectSpace.Delete(field);
            }

            foreach (var fieldDto in dto.Fields)
            {
                var existingField = existing.Fields.FirstOrDefault(f => f.FieldName == fieldDto.FieldName);
                if (existingField is not null)
                {
                    var fieldChanges = UpdateFieldTracked(existingField, fieldDto);
                    if (fieldChanges.Count > 0)
                        changes.Add($"field '{fieldDto.FieldName}': {string.Join(", ", fieldChanges)}");
                }
                else
                {
                    AddField(objectSpace, existing, fieldDto);
                    changes.Add($"added field '{fieldDto.FieldName}' ({fieldDto.TypeName})");
                }
            }

            return changes;
        }

        private static void AddField(IObjectSpace objectSpace, CustomClass cc, CustomFieldDto dto)
        {
            var field = objectSpace.CreateObject<CustomField>();
            field.CustomClass = cc;
            field.FieldName = dto.FieldName;
            ApplyFieldValues(field, dto);
        }

        private static void ApplyFieldValues(CustomField field, CustomFieldDto dto)
        {
            field.TypeName = dto.TypeName ?? "System.String";
            field.IsRequired = dto.IsRequired;
            field.Description = dto.Description;
            field.ReferencedClassName = dto.ReferencedClassName;
            field.SortOrder = dto.SortOrder;
            field.IsImmediatePostData = dto.IsImmediatePostData;
            field.StringMaxLength = dto.StringMaxLength;
            field.IsVisibleInListView = dto.IsVisibleInListView;
            field.IsVisibleInDetailView = dto.IsVisibleInDetailView;
            field.IsEditable = dto.IsEditable;
            field.ToolTip = dto.ToolTip;
            field.DisplayName = dto.DisplayName;
        }

        private static List<string> UpdateFieldTracked(CustomField field, CustomFieldDto dto)
        {
            var changes = new List<string>();

            if (field.TypeName != (dto.TypeName ?? "System.String"))
                changes.Add($"TypeName: '{field.TypeName}' -> '{dto.TypeName}'");
            if (field.IsRequired != dto.IsRequired)
                changes.Add($"IsRequired: {field.IsRequired} -> {dto.IsRequired}");
            if (field.ReferencedClassName != dto.ReferencedClassName)
                changes.Add("ReferencedClassName changed");
            if (field.SortOrder != dto.SortOrder)
                changes.Add($"SortOrder: {field.SortOrder} -> {dto.SortOrder}");
            if (field.IsVisibleInListView != dto.IsVisibleInListView)
                changes.Add($"IsVisibleInListView: {field.IsVisibleInListView} -> {dto.IsVisibleInListView}");
            if (field.IsVisibleInDetailView != dto.IsVisibleInDetailView)
                changes.Add($"IsVisibleInDetailView: {field.IsVisibleInDetailView} -> {dto.IsVisibleInDetailView}");
            if (field.IsEditable != dto.IsEditable)
                changes.Add($"IsEditable: {field.IsEditable} -> {dto.IsEditable}");

            ApplyFieldValues(field, dto);
            return changes;
        }
    }

    public record SchemaPackageDto
    {
        public string Version { get; init; }
        public DateTime ExportedAt { get; init; }
        public List<CustomClassDto> Classes { get; init; } = new();
    }

    public record CustomClassDto
    {
        public string ClassName { get; init; }
        public string NavigationGroup { get; init; }
        public string Description { get; init; }
        public bool IsApiExposed { get; init; }
        public List<CustomFieldDto> Fields { get; init; } = new();
    }

    public record CustomFieldDto
    {
        public string FieldName { get; init; }
        public string TypeName { get; init; }
        public bool IsRequired { get; init; }
        public string Description { get; init; }
        public string ReferencedClassName { get; init; }
        public int SortOrder { get; init; }
        public bool IsImmediatePostData { get; init; }
        public int? StringMaxLength { get; init; }
        public bool IsVisibleInListView { get; init; } = true;
        public bool IsVisibleInDetailView { get; init; } = true;
        public bool IsEditable { get; init; } = true;
        public string ToolTip { get; init; }
        public string DisplayName { get; init; }
    }

    public record SchemaImportResult(bool Success, string Message)
    {
        public string Details { get; init; }
    }
}
