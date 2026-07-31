using System.ComponentModel;
using System.Text;
using System.Text.Json;
using DevExpress.ExpressApp;
using LlmTornado.Chat;
using LlmTornado.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XafDynamicAssemblies.Module.BusinessObjects;

namespace XafDynamicAssemblies.Module.Services;

/// <summary>
/// Provides AI tool functions for schema management — listing, creating, modifying,
/// and deleting runtime entities, plus role permission management.
/// </summary>
public sealed class SchemaAIToolsProvider
{
    private readonly IServiceProvider _serviceProvider;
    private readonly SchemaDiscoveryService _discoveryService;
    private readonly ILogger<SchemaAIToolsProvider> _logger;
    private List<AIFunction> _tools;

    public SchemaAIToolsProvider(IServiceProvider serviceProvider, SchemaDiscoveryService discoveryService)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
        _logger = serviceProvider.GetRequiredService<ILogger<SchemaAIToolsProvider>>();
    }

    public IReadOnlyList<AIFunction> Tools => _tools ??= CreateTools();

    private List<AIFunction> CreateTools()
    {
        return new List<AIFunction>
        {
            // Read tools
            AIFunctionFactory.Create(ListEntities, "list_entities"),
            AIFunctionFactory.Create(DescribeEntity, "describe_entity"),
            AIFunctionFactory.Create(GetActiveSchema, "get_active_schema"),
            AIFunctionFactory.Create(GetPendingChanges, "get_pending_changes"),
            AIFunctionFactory.Create(ValidateSchema, "validate_schema"),
            // Write tools
            AIFunctionFactory.Create(CreateEntity, "create_entity"),
            AIFunctionFactory.Create(ModifyEntity, "modify_entity"),
            AIFunctionFactory.Create(DeleteEntity, "delete_entity"),
            // Role tools
            AIFunctionFactory.Create(ListRoles, "list_roles"),
            AIFunctionFactory.Create(SetRolePermissions, "set_role_permissions"),
            // Metadata action tools (live DetailView buttons — ACT-001)
            AIFunctionFactory.Create(ListActions, "list_actions"),
            AIFunctionFactory.Create(CreateAction, "create_action"),
            AIFunctionFactory.Create(DeleteAction, "delete_action"),
            AIFunctionFactory.Create(SetActionActive, "set_action_active"),
        };
    }

    /// <summary>
    /// Converts AIFunction definitions to LLMTornado Tool format for sending to the LLM.
    /// </summary>
    public IReadOnlyList<Tool> GetTornadoTools()
    {
        var tornadoTools = new List<Tool>();
        foreach (var fn in Tools)
        {
            var toolFunction = new ToolFunction(fn.Name, fn.Description, fn.JsonSchema);
            tornadoTools.Add(new Tool(toolFunction));
        }
        return tornadoTools;
    }

    // -- Helpers ---------------------------------------------------------------

    private sealed class ScopedObjectSpace : IDisposable
    {
        public IObjectSpace Os { get; }
        private readonly IServiceScope _scope;

        public ScopedObjectSpace(IObjectSpace os, IServiceScope scope)
        {
            Os = os;
            _scope = scope;
        }

        public void Dispose()
        {
            Os.Dispose();
            _scope?.Dispose();
        }
    }

    private ScopedObjectSpace CreateObjectSpace()
    {
        var scope = _serviceProvider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<INonSecuredObjectSpaceFactory>();
        var os = factory.CreateNonSecuredObjectSpace<CustomClass>();
        return new ScopedObjectSpace(os, scope);
    }

    private ScopedObjectSpace CreateObjectSpaceForType(Type type)
    {
        var scope = _serviceProvider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<INonSecuredObjectSpaceFactory>();
        var os = factory.CreateNonSecuredObjectSpace(type);
        return new ScopedObjectSpace(os, scope);
    }

    // ==========================================================================
    // READ TOOLS
    // ==========================================================================

    [Description("List all runtime entities (CustomClasses) with their field count, status, and API exposure. Returns a markdown table.")]
    private string ListEntities()
    {
        _logger.LogInformation("[Tool:list_entities] Called");
        try
        {
            using var scope = CreateObjectSpace();
            var classes = scope.Os.GetObjectsQuery<CustomClass>()
                .OrderBy(c => c.ClassName)
                .ToList();

            if (classes.Count == 0)
                return "No runtime entities defined yet. Use `create_entity` to create one.";

            var sb = new StringBuilder();
            sb.AppendLine("| Class Name | Fields | Status | API Exposed |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var cc in classes)
            {
                var fieldCount = cc.Fields?.Count ?? 0;
                sb.AppendLine($"| {cc.ClassName} | {fieldCount} | {cc.Status} | {(cc.IsApiExposed ? "Yes" : "No")} |");
            }
            sb.AppendLine();
            sb.AppendLine($"Total: {classes.Count} entities");

            var result = sb.ToString();
            _logger.LogInformation("[Tool:list_entities] Returning {Count} entities", classes.Count);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Tool:list_entities] Error");
            return $"Error listing entities: {ex.Message}";
        }
    }

    [Description("Get full details for a single runtime entity — all fields with types, required flags, references, and descriptions.")]
    private string DescribeEntity(
        [Description("The class name of the entity to describe (e.g. 'Employee', 'Product').")] string entityName)
    {
        _logger.LogInformation("[Tool:describe_entity] Called with entity={Entity}", entityName);
        try
        {
            if (string.IsNullOrWhiteSpace(entityName))
                return "Error: entityName parameter is required.";

            using var scope = CreateObjectSpace();
            var cc = scope.Os.GetObjectsQuery<CustomClass>()
                .FirstOrDefault(c => c.ClassName == entityName);

            if (cc == null)
            {
                var available = string.Join(", ", scope.Os.GetObjectsQuery<CustomClass>()
                    .Select(c => c.ClassName).OrderBy(n => n));
                return $"Entity '{entityName}' not found. Available entities: {(string.IsNullOrEmpty(available) ? "none" : available)}";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"## {cc.ClassName}");
            if (!string.IsNullOrWhiteSpace(cc.Description))
                sb.AppendLine(cc.Description);
            sb.AppendLine();
            sb.AppendLine($"- **Status:** {cc.Status}");
            sb.AppendLine($"- **Navigation Group:** {cc.NavigationGroup ?? "(none)"}");
            sb.AppendLine($"- **API Exposed:** {(cc.IsApiExposed ? "Yes" : "No")}");
            sb.AppendLine();

            var fields = cc.Fields?.OrderBy(f => f.SortOrder).ThenBy(f => f.FieldName).ToList();
            if (fields == null || fields.Count == 0)
            {
                sb.AppendLine("No fields defined.");
            }
            else
            {
                sb.AppendLine("| Field Name | Type | Required | Default | Reference | Description |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var f in fields)
                {
                    var typeName = !string.IsNullOrWhiteSpace(f.ReferencedClassName)
                        ? $"Reference({f.ReferencedClassName})"
                        : (f.TypeName ?? "System.String");
                    var required = f.IsRequired ? "Yes" : "No";
                    var isDefault = f.IsDefaultField ? "Yes" : "";
                    var refClass = f.ReferencedClassName ?? "";
                    var desc = f.Description ?? "";
                    sb.AppendLine($"| {f.FieldName} | {typeName} | {required} | {isDefault} | {refClass} | {desc} |");
                }
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Tool:describe_entity] Error");
            return $"Error describing entity: {ex.Message}";
        }
    }

    [Description("Show the currently live runtime types (loaded in memory) and compiled entities. Useful to see what is actually deployed vs. what is only in metadata.")]
    private string GetActiveSchema()
    {
        _logger.LogInformation("[Tool:get_active_schema] Called");
        try
        {
            var sb = new StringBuilder();

            // Runtime types currently loaded
            var runtimeTypes = XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes;
            sb.AppendLine("## Live Runtime Types");
            if (runtimeTypes.Length == 0)
            {
                sb.AppendLine("No runtime types currently loaded.");
            }
            else
            {
                foreach (var type in runtimeTypes.OrderBy(t => t.Name))
                {
                    var props = type.GetProperties()
                        .Where(p => p.DeclaringType == type)
                        .Select(p => $"{p.Name}: {p.PropertyType.Name}");
                    sb.AppendLine($"- **{type.Name}**: {string.Join(", ", props)}");
                }
            }
            sb.AppendLine();

            // Compiled entities from SchemaDiscoveryService
            var schema = _discoveryService.GetSchema();
            sb.AppendLine("## Compiled Entities");
            if (schema.CompiledEntities.Count == 0)
            {
                sb.AppendLine("No compiled entities discovered.");
            }
            else
            {
                foreach (var name in schema.CompiledEntities.OrderBy(n => n))
                    sb.AppendLine($"- {name}");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Tool:get_active_schema] Error");
            return $"Error getting active schema: {ex.Message}";
        }
    }

    [Description("Compare metadata (CustomClass definitions) against the currently live runtime types to show what changes are pending deployment.")]
    private string GetPendingChanges()
    {
        _logger.LogInformation("[Tool:get_pending_changes] Called");
        try
        {
            using var scope = CreateObjectSpace();
            var metadataClasses = scope.Os.GetObjectsQuery<CustomClass>()
                .Where(c => c.Status == CustomClassStatus.Runtime)
                .ToList();

            var liveTypes = XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes;
            var liveTypeNames = new HashSet<string>(liveTypes.Select(t => t.Name));
            var metadataNames = new HashSet<string>(metadataClasses.Select(c => c.ClassName));

            var newEntities = metadataNames.Except(liveTypeNames).OrderBy(n => n).ToList();
            var removedEntities = liveTypeNames.Except(metadataNames).OrderBy(n => n).ToList();
            var existingEntities = metadataNames.Intersect(liveTypeNames).OrderBy(n => n).ToList();

            var sb = new StringBuilder();

            if (newEntities.Count == 0 && removedEntities.Count == 0 && existingEntities.Count == 0)
            {
                sb.AppendLine("No runtime entities in metadata and no live runtime types. Nothing pending.");
                return sb.ToString();
            }

            if (newEntities.Count > 0)
            {
                sb.AppendLine("## New (will be created on Deploy)");
                foreach (var name in newEntities)
                    sb.AppendLine($"- {name}");
                sb.AppendLine();
            }

            if (removedEntities.Count > 0)
            {
                sb.AppendLine("## Removed (live but no longer in metadata)");
                foreach (var name in removedEntities)
                    sb.AppendLine($"- {name}");
                sb.AppendLine();
            }

            if (existingEntities.Count > 0)
            {
                sb.AppendLine("## Existing (may have field changes)");
                foreach (var name in existingEntities)
                {
                    var cc = metadataClasses.First(c => c.ClassName == name);
                    var liveType = liveTypes.First(t => t.Name == name);
                    var liveProps = new HashSet<string>(
                        liveType.GetProperties()
                            .Where(p => p.DeclaringType == liveType)
                            .Select(p => p.Name));
                    var metaFields = new HashSet<string>(
                        cc.Fields?.Select(f => f.FieldName) ?? Enumerable.Empty<string>());

                    var addedFields = metaFields.Except(liveProps).ToList();
                    var removedFields = liveProps.Except(metaFields).ToList();

                    if (addedFields.Count > 0 || removedFields.Count > 0)
                    {
                        sb.AppendLine($"- **{name}**: ");
                        if (addedFields.Count > 0)
                            sb.AppendLine($"  - Added fields: {string.Join(", ", addedFields)}");
                        if (removedFields.Count > 0)
                            sb.AppendLine($"  - Removed fields: {string.Join(", ", removedFields)}");
                    }
                    else
                    {
                        sb.AppendLine($"- **{name}**: no field changes detected");
                    }
                }
                sb.AppendLine();
            }

            if (newEntities.Count == 0 && removedEntities.Count == 0)
                sb.AppendLine("_No structural changes detected. Deploy will still recompile and restart._");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Tool:get_pending_changes] Error");
            return $"Error checking pending changes: {ex.Message}";
        }
    }

    [Description("Validate the current schema by running a test compilation via Roslyn. Reports any compilation errors or warnings without actually deploying.")]
    private string ValidateSchema()
    {
        _logger.LogInformation("[Tool:validate_schema] Called");
        try
        {
            using var scope = CreateObjectSpace();
            var classes = scope.Os.GetObjectsQuery<CustomClass>()
                .Where(c => c.Status == CustomClassStatus.Runtime)
                .ToList();

            if (classes.Count == 0)
                return "No runtime entities to validate. Create some entities first.";

            var result = RuntimeAssemblyBuilder.ValidateCompilation(classes);

            var sb = new StringBuilder();
            if (result.Success)
            {
                sb.AppendLine("Compilation **successful**.");
                sb.AppendLine($"- {classes.Count} class(es) compiled");
                if (result.Warnings.Count > 0)
                {
                    sb.AppendLine($"- {result.Warnings.Count} warning(s):");
                    foreach (var w in result.Warnings.Take(10))
                        sb.AppendLine($"  - {w}");
                }
                else
                {
                    sb.AppendLine("- No warnings");
                }
            }
            else
            {
                sb.AppendLine("Compilation **failed**.");
                sb.AppendLine($"- {result.Errors.Count} error(s):");
                foreach (var e in result.Errors.Take(20))
                    sb.AppendLine($"  - {e}");
                if (result.Warnings.Count > 0)
                {
                    sb.AppendLine($"- {result.Warnings.Count} warning(s):");
                    foreach (var w in result.Warnings.Take(10))
                        sb.AppendLine($"  - {w}");
                }
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Tool:validate_schema] Error");
            return $"Error validating schema: {ex.Message}";
        }
    }

    // ==========================================================================
    // WRITE TOOLS
    // ==========================================================================

    [Description("Create a new runtime entity (CustomClass) with fields. After creation, call validate_schema to check for errors, then the user must Deploy to make it live.")]
    private string CreateEntity(
        [Description("PascalCase class name for the entity (e.g. 'Employee', 'ProductCategory').")] string className,
        [Description("XAF navigation group name (e.g. 'HR', 'Inventory'). Optional.")] string navigationGroup,
        [Description("Description of what this entity represents. Optional.")] string description,
        [Description("JSON array of field definitions. Each object: {\"name\": \"FieldName\", \"type\": \"System.String\", \"required\": false, \"referencedClass\": null, \"description\": \"...\"}. Type defaults to System.String if omitted.")] string fieldsJson)
    {
        _logger.LogInformation("[Tool:create_entity] Creating {Name}", className);
        try
        {
            if (string.IsNullOrWhiteSpace(className))
                return "Error: className is required.";

            using var scope = CreateObjectSpace();
            var existing = scope.Os.GetObjectsQuery<CustomClass>()
                .FirstOrDefault(c => c.ClassName == className);
            if (existing != null)
                return $"Error: Entity '{className}' already exists. Use modify_entity to change it.";

            var cc = scope.Os.CreateObject<CustomClass>();
            cc.ClassName = className;
            cc.NavigationGroup = navigationGroup;
            cc.Description = description;
            cc.Status = CustomClassStatus.Runtime;

            if (!string.IsNullOrWhiteSpace(fieldsJson))
            {
                var fieldDefs = JsonSerializer.Deserialize<List<FieldDefinition>>(fieldsJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (fieldDefs != null)
                {
                    int sortOrder = 0;
                    foreach (var fd in fieldDefs)
                    {
                        var field = scope.Os.CreateObject<CustomField>();
                        field.CustomClass = cc;
                        field.FieldName = fd.Name;
                        field.TypeName = string.IsNullOrWhiteSpace(fd.ReferencedClass)
                            ? (fd.Type ?? "System.String")
                            : "Reference";
                        field.IsRequired = fd.Required;
                        field.ReferencedClassName = fd.ReferencedClass;
                        field.Description = fd.Description;
                        field.SortOrder = sortOrder++;
                        if (sortOrder == 1)
                            field.IsDefaultField = true;
                        cc.Fields.Add(field);
                    }
                }
            }

            scope.Os.CommitChanges();

            var fieldCount = cc.Fields?.Count ?? 0;
            _logger.LogInformation("[Tool:create_entity] Created {Name} with {Fields} fields", className, fieldCount);
            return $"Entity '{className}' created with {fieldCount} field(s). Run `validate_schema` to check for compilation errors, then Deploy to make it live.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Tool:create_entity] Error");
            return $"Error creating entity: {ex.Message}";
        }
    }

    [Description("Modify an existing runtime entity — add fields, remove fields, update fields, or change class-level properties. After modification, call validate_schema then Deploy.")]
    private string ModifyEntity(
        [Description("The class name of the entity to modify.")] string entityName,
        [Description("JSON object with modifications: {\"addFields\": [{\"name\": \"...\", \"type\": \"...\", \"required\": false, \"referencedClass\": null}], \"removeFields\": [\"FieldName\"], \"updateFields\": [{\"name\": \"ExistingField\", \"type\": \"System.Int32\", \"required\": true}], \"navigationGroup\": \"NewGroup\", \"description\": \"New desc\", \"isApiExposed\": true}")] string modificationsJson)
    {
        _logger.LogInformation("[Tool:modify_entity] Modifying {Name}", entityName);
        try
        {
            if (string.IsNullOrWhiteSpace(entityName))
                return "Error: entityName is required.";
            if (string.IsNullOrWhiteSpace(modificationsJson))
                return "Error: modificationsJson is required.";

            using var scope = CreateObjectSpace();
            var cc = scope.Os.GetObjectsQuery<CustomClass>()
                .FirstOrDefault(c => c.ClassName == entityName);

            if (cc == null)
            {
                var available = string.Join(", ", scope.Os.GetObjectsQuery<CustomClass>()
                    .Select(c => c.ClassName).OrderBy(n => n));
                return $"Entity '{entityName}' not found. Available: {(string.IsNullOrEmpty(available) ? "none" : available)}";
            }

            if (cc.Status == CustomClassStatus.Compiled)
                return $"Error: Entity '{entityName}' has been graduated (Status=Compiled) and cannot be modified at runtime.";

            var mods = JsonSerializer.Deserialize<ModificationsPayload>(modificationsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (mods == null)
                return "Error: Could not parse modificationsJson.";

            var changes = new List<string>();

            // Update class-level properties
            if (mods.NavigationGroup != null)
            {
                cc.NavigationGroup = mods.NavigationGroup;
                changes.Add($"NavigationGroup -> '{mods.NavigationGroup}'");
            }
            if (mods.Description != null)
            {
                cc.Description = mods.Description;
                changes.Add($"Description updated");
            }
            if (mods.IsApiExposed.HasValue)
            {
                cc.IsApiExposed = mods.IsApiExposed.Value;
                changes.Add($"IsApiExposed -> {mods.IsApiExposed.Value}");
            }

            // Remove fields
            if (mods.RemoveFields != null)
            {
                foreach (var fieldName in mods.RemoveFields)
                {
                    var field = cc.Fields?.FirstOrDefault(f => f.FieldName == fieldName);
                    if (field != null)
                    {
                        scope.Os.Delete(field);
                        changes.Add($"Removed field '{fieldName}'");
                    }
                    else
                    {
                        changes.Add($"Field '{fieldName}' not found (skipped)");
                    }
                }
            }

            // Add fields
            if (mods.AddFields != null)
            {
                var maxSort = cc.Fields?.Max(f => (int?)f.SortOrder) ?? -1;
                foreach (var fd in mods.AddFields)
                {
                    var existing = cc.Fields?.FirstOrDefault(f => f.FieldName == fd.Name);
                    if (existing != null)
                    {
                        changes.Add($"Field '{fd.Name}' already exists (skipped add)");
                        continue;
                    }

                    var field = scope.Os.CreateObject<CustomField>();
                    field.CustomClass = cc;
                    field.FieldName = fd.Name;
                    field.TypeName = string.IsNullOrWhiteSpace(fd.ReferencedClass)
                        ? (fd.Type ?? "System.String")
                        : "Reference";
                    field.IsRequired = fd.Required;
                    field.ReferencedClassName = fd.ReferencedClass;
                    field.Description = fd.Description;
                    field.SortOrder = ++maxSort;
                    cc.Fields.Add(field);
                    changes.Add($"Added field '{fd.Name}' ({field.TypeName})");
                }
            }

            // Update existing fields
            if (mods.UpdateFields != null)
            {
                foreach (var fd in mods.UpdateFields)
                {
                    var field = cc.Fields?.FirstOrDefault(f => f.FieldName == fd.Name);
                    if (field == null)
                    {
                        changes.Add($"Field '{fd.Name}' not found for update (skipped)");
                        continue;
                    }

                    if (fd.Type != null)
                    {
                        field.TypeName = string.IsNullOrWhiteSpace(fd.ReferencedClass)
                            ? fd.Type
                            : "Reference";
                    }
                    if (fd.ReferencedClass != null)
                        field.ReferencedClassName = fd.ReferencedClass;
                    field.IsRequired = fd.Required;
                    if (fd.Description != null)
                        field.Description = fd.Description;
                    changes.Add($"Updated field '{fd.Name}'");
                }
            }

            scope.Os.CommitChanges();

            var summary = changes.Count > 0
                ? string.Join("\n- ", changes)
                : "No changes applied";
            _logger.LogInformation("[Tool:modify_entity] Modified {Name}: {Changes}", entityName, changes.Count);
            return $"Entity '{entityName}' modified:\n- {summary}\n\nRun `validate_schema` to check, then Deploy.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Tool:modify_entity] Error");
            return $"Error modifying entity: {ex.Message}";
        }
    }

    [Description("Delete a runtime entity and all its fields. Cannot delete entities with Status=Compiled (graduated). After deletion, Deploy to remove the live type.")]
    private string DeleteEntity(
        [Description("The class name of the entity to delete.")] string entityName)
    {
        _logger.LogInformation("[Tool:delete_entity] Deleting {Name}", entityName);
        try
        {
            if (string.IsNullOrWhiteSpace(entityName))
                return "Error: entityName is required.";

            using var scope = CreateObjectSpace();
            var cc = scope.Os.GetObjectsQuery<CustomClass>()
                .FirstOrDefault(c => c.ClassName == entityName);

            if (cc == null)
            {
                var available = string.Join(", ", scope.Os.GetObjectsQuery<CustomClass>()
                    .Select(c => c.ClassName).OrderBy(n => n));
                return $"Entity '{entityName}' not found. Available: {(string.IsNullOrEmpty(available) ? "none" : available)}";
            }

            if (cc.Status == CustomClassStatus.Compiled)
                return $"Error: Entity '{entityName}' has been graduated (Status=Compiled) and cannot be deleted. Remove it from the codebase instead.";

            var fieldCount = cc.Fields?.Count ?? 0;

            // Delete fields first, then the class
            if (cc.Fields != null)
            {
                foreach (var field in cc.Fields.ToList())
                    scope.Os.Delete(field);
            }
            scope.Os.Delete(cc);
            scope.Os.CommitChanges();

            _logger.LogInformation("[Tool:delete_entity] Deleted {Name} with {Fields} fields", entityName, fieldCount);
            return $"Entity '{entityName}' and its {fieldCount} field(s) deleted. Deploy to remove the live type.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Tool:delete_entity] Error");
            return $"Error deleting entity: {ex.Message}";
        }
    }

    // ==========================================================================
    // ROLE TOOLS
    // ==========================================================================

    [Description("List all security roles in the application. Returns role names and whether they are admin roles.")]
    private string ListRoles()
    {
        _logger.LogInformation("[Tool:list_roles] Called");
        try
        {
            // Use dynamic to avoid hard dependency on DevExpress.ExpressApp.Security
            Type roleType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                roleType = asm.GetType("DevExpress.Persistent.BaseImpl.EF.PermissionPolicy.PermissionPolicyRole");
                if (roleType != null) break;
            }

            if (roleType == null)
                return "Security module is not configured in this application. Role management is not available.";

            var scope = _serviceProvider.CreateScope();
            IObjectSpace os;
            try
            {
                var factory = scope.ServiceProvider.GetRequiredService<INonSecuredObjectSpaceFactory>();
                os = factory.CreateNonSecuredObjectSpace(roleType);
            }
            catch
            {
                scope.Dispose();
                return "Security module is not configured or INonSecuredObjectSpaceFactory is not available for role types.";
            }

            using (new ScopedObjectSpace(os, scope))
            {
                var roles = os.GetObjects(roleType).Cast<object>().ToList();

                if (roles.Count == 0)
                    return "No roles found in the application.";

                var sb = new StringBuilder();
                sb.AppendLine("| Role Name | Is Admin |");
                sb.AppendLine("|---|---|");
                foreach (dynamic role in roles)
                {
                    try
                    {
                        string name = role.Name;
                        bool isAdmin = role.IsAdministrative;
                        sb.AppendLine($"| {name} | {(isAdmin ? "Yes" : "No")} |");
                    }
                    catch
                    {
                        sb.AppendLine($"| (error reading role) | ? |");
                    }
                }

                return sb.ToString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Tool:list_roles] Error");
            return $"Error listing roles: {ex.Message}";
        }
    }

    [Description("Set type-level permissions for a role on a specific entity. Configures read, write, create, and delete access.")]
    private string SetRolePermissions(
        [Description("The name of the role to modify (e.g. 'Users', 'Managers').")] string roleName,
        [Description("The entity name to set permissions for (e.g. 'Employee'). Can be a runtime or compiled entity.")] string entityName,
        [Description("Allow read access. Defaults to true.")] bool allowRead,
        [Description("Allow write/update access. Defaults to true.")] bool allowWrite,
        [Description("Allow creating new records. Defaults to true.")] bool allowCreate,
        [Description("Allow deleting records. Defaults to false.")] bool allowDelete)
    {
        _logger.LogInformation("[Tool:set_role_permissions] Role={Role}, Entity={Entity}", roleName, entityName);
        try
        {
            if (string.IsNullOrWhiteSpace(roleName))
                return "Error: roleName is required.";
            if (string.IsNullOrWhiteSpace(entityName))
                return "Error: entityName is required.";

            // Find the role type
            Type roleType = null;
            Type permissionType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                roleType ??= asm.GetType("DevExpress.Persistent.BaseImpl.EF.PermissionPolicy.PermissionPolicyRole");
                permissionType ??= asm.GetType("DevExpress.Persistent.BaseImpl.EF.PermissionPolicy.PermissionPolicyTypePermissionObject");
                if (roleType != null && permissionType != null) break;
            }

            if (roleType == null || permissionType == null)
                return "Security module is not configured in this application. Role management is not available.";

            // Find the target entity type
            Type targetType = null;
            // Check runtime types first
            targetType = XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes
                .FirstOrDefault(t => t.Name == entityName);
            // Check compiled types
            if (targetType == null)
            {
                foreach (var typeInfo in XafTypesInfo.Instance.PersistentTypes)
                {
                    if (typeInfo.Name == entityName)
                    {
                        targetType = typeInfo.Type;
                        break;
                    }
                }
            }

            if (targetType == null)
                return $"Error: Entity '{entityName}' not found among runtime or compiled types.";

            // Find SecurityStrategyComplex operations enum
            Type operationsEnum = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                operationsEnum = asm.GetType("DevExpress.Persistent.Base.SecurityStrategyOperations");
                if (operationsEnum != null) break;
            }

            using var scopedOs = CreateObjectSpaceForType(roleType);
            var os = scopedOs.Os;

            // Find the role
            dynamic role = os.GetObjects(roleType).Cast<object>()
                .FirstOrDefault(r => ((dynamic)r).Name == roleName);

            if (role == null)
            {
                var availableRoles = string.Join(", ",
                    os.GetObjects(roleType).Cast<object>().Select(r => ((dynamic)r).Name?.ToString()));
                return $"Role '{roleName}' not found. Available roles: {(string.IsNullOrEmpty(availableRoles) ? "none" : availableRoles)}";
            }

            // Use XAF's permission policy API
            // PermissionPolicyRole has EnsureTypePermissions / SetTypePermission methods
            try
            {
                // Build operations string
                var ops = new List<string>();
                if (allowRead) ops.Add("Read");
                if (allowWrite) ops.Add("Write");
                if (allowCreate) ops.Add("Create");
                if (allowDelete) ops.Add("Delete");
                var operationsStr = string.Join(";", ops);

                // Use the AddTypePermission approach via reflection
                // PermissionPolicyRole.AddTypePermissionsRecursively<T>(string operations, SecurityPermissionState state)
                var addMethod = roleType.GetMethods()
                    .FirstOrDefault(m => m.Name == "AddTypePermissionsRecursively" && m.GetParameters().Length == 2 && !m.IsGenericMethod);

                if (addMethod != null)
                {
                    // Get SecurityPermissionState.Allow
                    Type stateEnum = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        stateEnum = asm.GetType("DevExpress.Persistent.Base.SecurityPermissionState");
                        if (stateEnum != null) break;
                    }

                    if (stateEnum != null)
                    {
                        var allowState = Enum.Parse(stateEnum, "Allow");
                        var denyState = Enum.Parse(stateEnum, "Deny");

                        // Set allowed operations
                        if (ops.Count > 0)
                            addMethod.Invoke(role, new object[] { targetType, operationsStr, allowState });

                        // Set denied operations
                        var denyOps = new List<string>();
                        if (!allowRead) denyOps.Add("Read");
                        if (!allowWrite) denyOps.Add("Write");
                        if (!allowCreate) denyOps.Add("Create");
                        if (!allowDelete) denyOps.Add("Delete");

                        if (denyOps.Count > 0)
                            addMethod.Invoke(role, new object[] { targetType, string.Join(";", denyOps), denyState });
                    }
                }
                else
                {
                    return "Error: Could not find AddTypePermissionsRecursively method on the role type. Security API may have changed.";
                }
            }
            catch (Exception ex)
            {
                return $"Error setting permissions via API: {ex.Message}";
            }

            os.CommitChanges();

            var summary = new StringBuilder();
            summary.AppendLine($"Permissions set for role '{roleName}' on entity '{entityName}':");
            summary.AppendLine($"- Read: {(allowRead ? "Allow" : "Deny")}");
            summary.AppendLine($"- Write: {(allowWrite ? "Allow" : "Deny")}");
            summary.AppendLine($"- Create: {(allowCreate ? "Allow" : "Deny")}");
            summary.AppendLine($"- Delete: {(allowDelete ? "Allow" : "Deny")}");
            return summary.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Tool:set_role_permissions] Error");
            return $"Error setting role permissions: {ex.Message}";
        }
    }

    // ==========================================================================
    // METADATA ACTION TOOLS (live DetailView buttons — ACT-001)
    // ==========================================================================

    [Description("List metadata actions (live DetailView buttons): caption, target entity, active state, steps, criteria. Returns a markdown table.")]
    private string ListActions(
        [Description("Only list actions for this target entity. Optional — pass empty to list all.")] string entityName)
    {
        _logger.LogInformation("[Tool:list_actions] Called with entity={Entity}", entityName);
        try
        {
            using var scope = CreateObjectSpace();
            var query = scope.Os.GetObjectsQuery<CustomAction>();
            if (!string.IsNullOrWhiteSpace(entityName))
                query = query.Where(a => a.TargetEntity == entityName);
            var actions = query.OrderBy(a => a.TargetEntity).ThenBy(a => a.Caption).ToList();

            if (actions.Count == 0)
                return string.IsNullOrWhiteSpace(entityName)
                    ? "No metadata actions defined yet. Use `create_action` to add a live button to an entity's detail view."
                    : $"No metadata actions defined for '{entityName}'. Use `create_action` to add one.";

            var sb = new StringBuilder();
            sb.AppendLine("| Caption | Target Entity | Active | Steps | Criteria |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var a in actions)
            {
                var steps = string.Join("; ", (a.Steps ?? Enumerable.Empty<CustomActionStep>().ToList())
                    .OrderBy(s => s.SortOrder).Select(s => s.DisplayText));
                sb.AppendLine($"| {a.Caption} | {a.TargetEntity} | {(a.IsActive ? "Yes" : "No")} | {steps} | {a.Criteria} |");
            }
            sb.AppendLine();
            sb.AppendLine($"Total: {actions.Count} action(s)");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Tool:list_actions] Error");
            return $"Error listing actions: {ex.Message}";
        }
    }

    [Description("Create a metadata action — a button on an entity's DetailView that runs steps (SetField, ShowMessage, OpenView). LIVE the next time the detail view opens: no deploy, no restart.")]
    private string CreateAction(
        [Description("Button caption (e.g. 'Approve'). Must be unique per target entity.")] string caption,
        [Description("Entity whose DetailView gets the button (simple name, e.g. 'Order'). May be a runtime or compiled entity.")] string targetEntity,
        [Description("XAF criteria string enabling the button conditionally (e.g. \"Status != 'Approved'\"). Optional.")] string criteria,
        [Description("Confirmation prompt shown before executing. Optional.")] string confirmationMessage,
        [Description("JSON array of steps, executed in array order. Each: {\"kind\": \"SetField\"|\"ShowMessage\"|\"OpenView\", \"fieldName\": \"...\", \"value\": \"...\", \"messageText\": \"...\", \"messageType\": \"Info\"|\"Success\"|\"Warning\"|\"Error\", \"targetEntityName\": \"...\"}. SetField needs fieldName (+value); ShowMessage needs messageText; OpenView needs targetEntityName. At most one OpenView step.")] string stepsJson)
    {
        _logger.LogInformation("[Tool:create_action] Creating {Caption} on {Entity}", caption, targetEntity);
        try
        {
            if (string.IsNullOrWhiteSpace(caption))
                return "Error: caption is required.";
            if (string.IsNullOrWhiteSpace(targetEntity))
                return "Error: targetEntity is required.";

            using var scope = CreateObjectSpace();
            var duplicate = scope.Os.GetObjectsQuery<CustomAction>()
                .FirstOrDefault(a => a.Caption == caption && a.TargetEntity == targetEntity);
            if (duplicate != null)
                return $"Error: An action '{caption}' already exists on '{targetEntity}'. Use delete_action first, then recreate it.";

            List<StepDefinition> stepDefs;
            try
            {
                stepDefs = JsonSerializer.Deserialize<List<StepDefinition>>(stepsJson ?? "",
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException jex)
            {
                return $"Error: stepsJson is not valid JSON: {jex.Message}";
            }
            if (stepDefs == null || stepDefs.Count == 0)
                return "Error: stepsJson must contain at least one step — a button with no steps does nothing.";

            // Mirror the XAF save rules — they do not fire on this non-secured ObjectSpace.
            var parsed = new List<(StepKind Kind, StepDefinition Def)>();
            foreach (var sd in stepDefs)
            {
                if (!Enum.TryParse<StepKind>(sd.Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(typeof(StepKind), kind))
                    return $"Error: Unknown step kind '{sd.Kind}'. Valid kinds: SetField, ShowMessage, OpenView.";
                if (kind == StepKind.SetField && string.IsNullOrWhiteSpace(sd.FieldName))
                    return "Error: A SetField step requires fieldName.";
                if (kind == StepKind.ShowMessage && string.IsNullOrWhiteSpace(sd.MessageText))
                    return "Error: A ShowMessage step requires messageText.";
                if (kind == StepKind.OpenView && string.IsNullOrWhiteSpace(sd.TargetEntityName))
                    return "Error: An OpenView step requires targetEntityName.";
                parsed.Add((kind, sd));
            }
            if (parsed.Count(p => p.Kind == StepKind.OpenView) > 1)
                return "Error: An action may contain at most one OpenView step.";

            var warnings = new List<string>();
            if (!string.IsNullOrWhiteSpace(criteria))
            {
                try { DevExpress.Data.Filtering.CriteriaOperator.Parse(criteria); }
                catch { warnings.Add($"Criteria \"{criteria}\" could not be parsed — the button will be disabled until it is fixed."); }
            }

            var targetTypeExists =
                XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes.Any(t => t.Name == targetEntity)
                || XafTypesInfo.Instance.PersistentTypes.Any(ti => ti.Name == targetEntity);
            if (!targetTypeExists)
                warnings.Add($"Entity '{targetEntity}' is not currently a live runtime or compiled type — the button appears once that entity exists (created + deployed).");

            var activeCount = scope.Os.GetObjectsQuery<CustomAction>()
                .Count(a => a.TargetEntity == targetEntity && a.IsActive);
            if (activeCount >= 10)
                warnings.Add($"'{targetEntity}' already has {activeCount} active actions — the dispatcher renders at most 10 per entity, so this one may not appear until others are removed or deactivated.");

            var action = scope.Os.CreateObject<CustomAction>();
            action.Caption = caption;
            action.TargetEntity = targetEntity;
            action.Criteria = string.IsNullOrWhiteSpace(criteria) ? null : criteria;
            action.ConfirmationMessage = string.IsNullOrWhiteSpace(confirmationMessage) ? null : confirmationMessage;
            action.IsActive = true;

            for (var i = 0; i < parsed.Count; i++)
            {
                var (kind, sd) = parsed[i];
                var step = scope.Os.CreateObject<CustomActionStep>();
                step.CustomAction = action;
                step.SortOrder = i;
                step.Kind = kind;
                step.FieldName = sd.FieldName;
                step.Value = sd.Value;
                step.MessageText = sd.MessageText;
                step.MessageType = Enum.TryParse<StepMessageType>(sd.MessageType, ignoreCase: true, out var mt) && Enum.IsDefined(typeof(StepMessageType), mt)
                    ? mt : StepMessageType.Info;
                step.TargetEntityName = sd.TargetEntityName;
                action.Steps.Add(step);
            }

            scope.Os.CommitChanges();

            var sb = new StringBuilder();
            sb.AppendLine($"Action '{caption}' created on '{targetEntity}' with {parsed.Count} step(s).");
            sb.AppendLine("It is LIVE the next time that entity's detail view opens — no deploy, no restart needed.");
            foreach (var w in warnings)
                sb.AppendLine($"- Warning: {w}");
            _logger.LogInformation("[Tool:create_action] Created {Caption} on {Entity} with {Steps} steps",
                caption, targetEntity, parsed.Count);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Tool:create_action] Error");
            return $"Error creating action: {ex.Message}";
        }
    }

    [Description("Delete a metadata action (and its steps) identified by caption + target entity. The button disappears the next time the detail view opens.")]
    private string DeleteAction(
        [Description("The action's caption (e.g. 'Approve').")] string caption,
        [Description("The entity the action targets (e.g. 'Order').")] string targetEntity)
    {
        _logger.LogInformation("[Tool:delete_action] Deleting {Caption} on {Entity}", caption, targetEntity);
        try
        {
            if (string.IsNullOrWhiteSpace(caption) || string.IsNullOrWhiteSpace(targetEntity))
                return "Error: caption and targetEntity are required.";

            using var scope = CreateObjectSpace();
            var action = scope.Os.GetObjectsQuery<CustomAction>()
                .FirstOrDefault(a => a.Caption == caption && a.TargetEntity == targetEntity);
            if (action == null)
            {
                var available = string.Join(", ", scope.Os.GetObjectsQuery<CustomAction>()
                    .Where(a => a.TargetEntity == targetEntity)
                    .Select(a => a.Caption).OrderBy(c => c));
                return $"Action '{caption}' on '{targetEntity}' not found. Actions on that entity: {(string.IsNullOrEmpty(available) ? "none" : available)}";
            }

            var stepCount = action.Steps?.Count ?? 0;
            if (action.Steps != null)
            {
                foreach (var step in action.Steps.ToList())
                    scope.Os.Delete(step);
            }
            scope.Os.Delete(action);
            scope.Os.CommitChanges();

            _logger.LogInformation("[Tool:delete_action] Deleted {Caption} on {Entity}", caption, targetEntity);
            return $"Action '{caption}' on '{targetEntity}' deleted ({stepCount} step(s) removed). The button disappears the next time the detail view opens.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Tool:delete_action] Error");
            return $"Error deleting action: {ex.Message}";
        }
    }

    [Description("Enable or disable a metadata action without deleting it. Takes effect the next time the entity's detail view opens.")]
    private string SetActionActive(
        [Description("The action's caption (e.g. 'Approve').")] string caption,
        [Description("The entity the action targets (e.g. 'Order').")] string targetEntity,
        [Description("true to enable the button, false to hide it.")] bool isActive)
    {
        _logger.LogInformation("[Tool:set_action_active] {Caption} on {Entity} -> {Active}", caption, targetEntity, isActive);
        try
        {
            if (string.IsNullOrWhiteSpace(caption) || string.IsNullOrWhiteSpace(targetEntity))
                return "Error: caption and targetEntity are required.";

            using var scope = CreateObjectSpace();
            var action = scope.Os.GetObjectsQuery<CustomAction>()
                .FirstOrDefault(a => a.Caption == caption && a.TargetEntity == targetEntity);
            if (action == null)
            {
                var available = string.Join(", ", scope.Os.GetObjectsQuery<CustomAction>()
                    .Where(a => a.TargetEntity == targetEntity)
                    .Select(a => a.Caption).OrderBy(c => c));
                return $"Action '{caption}' on '{targetEntity}' not found. Actions on that entity: {(string.IsNullOrEmpty(available) ? "none" : available)}";
            }

            action.IsActive = isActive;
            scope.Os.CommitChanges();
            return $"Action '{caption}' on '{targetEntity}' is now {(isActive ? "active" : "inactive")}. Takes effect the next time the detail view opens.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Tool:set_action_active] Error");
            return $"Error setting action active state: {ex.Message}";
        }
    }

    // ==========================================================================
    // JSON DTOs for tool parameters
    // ==========================================================================

    private sealed class FieldDefinition
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public bool Required { get; set; }
        public string ReferencedClass { get; set; }
        public string Description { get; set; }
    }

    private sealed class ModificationsPayload
    {
        public List<FieldDefinition> AddFields { get; set; }
        public List<string> RemoveFields { get; set; }
        public List<FieldDefinition> UpdateFields { get; set; }
        public string NavigationGroup { get; set; }
        public string Description { get; set; }
        public bool? IsApiExposed { get; set; }
    }

    private sealed class StepDefinition
    {
        public string Kind { get; set; }
        public string FieldName { get; set; }
        public string Value { get; set; }
        public string MessageText { get; set; }
        public string MessageType { get; set; }
        public string TargetEntityName { get; set; }
    }
}
