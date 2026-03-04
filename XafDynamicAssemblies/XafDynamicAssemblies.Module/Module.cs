using System.Reflection;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Updating;
using DevExpress.Persistent.Base;
using Npgsql;
using XafDynamicAssemblies.Module.BusinessObjects;
using XafDynamicAssemblies.Module.Services;

namespace XafDynamicAssemblies.Module
{
    // For more typical usage scenarios, be sure to check out https://docs.devexpress.com/eXpressAppFramework/DevExpress.ExpressApp.ModuleBase.
    public sealed class XafDynamicAssembliesModule : ModuleBase
    {
        /// <summary>
        /// Set this before XAF application starts (e.g. in Startup.cs).
        /// Used to query metadata and sync schema at startup.
        /// </summary>
        public static string RuntimeConnectionString { get; set; }

        /// <summary>
        /// Shared manager for runtime assembly lifecycle.
        /// </summary>
        public static AssemblyGenerationManager AssemblyManager { get; } = new();

        /// <summary>
        /// Singleton module instance — set in constructor for cross-component access.
        /// </summary>
        public static XafDynamicAssembliesModule Instance { get; private set; }

        /// <summary>
        /// Reset all static state before an in-process restart.
        /// Called by Program.cs before rebuilding the host so that the next startup
        /// recompiles everything fresh, avoiding stale Type references across ALCs.
        /// </summary>
        public static void ResetForRestart()
        {
            AssemblyManager.UnloadCurrent();
            XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes = Array.Empty<Type>();
            Instance = null;
            CurrentApplication = null;
            DegradedMode = false;
            DegradedModeReason = null;
            ApiExposedClassNames = new();

            // Reset XAF's TypesInfo to force full re-initialization on next host.
            // Without this, the process-static TypesInfo retains type registrations
            // from the previous host, causing view model generation to fail for
            // runtime entity types compiled into a different assembly.
            XafTypesInfo.HardReset();

            // Clear DevExpress's shared application model manager cache.
            // This is a process-static cache that must be invalidated for the
            // new host to build a fresh application model with the recompiled types.
            ClearSharedModelManagerCache();
        }

        private static void ClearSharedModelManagerCache()
        {
            try
            {
                // DevExpress.ExpressApp.AspNetCore.Shared.SharedApplicationModelManagerContainer
                // is a static cache that persists across host rebuilds.
                // Use reflection to find and clear it.
                var blazorAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "DevExpress.ExpressApp.Blazor");
                if (blazorAsm == null) return;

                var containerType = blazorAsm.GetType(
                    "DevExpress.ExpressApp.AspNetCore.Shared.SharedApplicationModelManagerContainer");
                if (containerType == null) return;

                // Try to find a static instance or singleton
                var instanceField = containerType.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    .FirstOrDefault(f => f.FieldType == containerType || f.Name.Contains("instance", StringComparison.OrdinalIgnoreCase));
                if (instanceField != null)
                {
                    instanceField.SetValue(null, null);
                    return;
                }

                // Try to find and clear any static cache fields
                foreach (var field in containerType.GetFields(BindingFlags.Static | BindingFlags.NonPublic))
                {
                    if (field.FieldType.Name.Contains("Dictionary") || field.FieldType.Name.Contains("Concurrent"))
                    {
                        var val = field.GetValue(null);
                        var clearMethod = val?.GetType().GetMethod("Clear");
                        clearMethod?.Invoke(val, null);
                    }
                }
            }
            catch
            {
                // Non-fatal — worst case, the model cache is stale
            }
        }

        /// <summary>
        /// Reference to the running XafApplication, set during Setup.
        /// Used by SchemaChangeOrchestrator to update the model after hot-load.
        /// </summary>
        public static XafApplication CurrentApplication { get; private set; }

        /// <summary>
        /// Class names marked as API-exposed in metadata. Populated during EarlyBootstrap/BootstrapRuntimeEntities.
        /// Used by Startup.cs to register Web API endpoints for the correct subset of runtime types.
        /// </summary>
        public static HashSet<string> ApiExposedClassNames { get; private set; } = new();

        /// <summary>
        /// True when runtime entity compilation failed at startup.
        /// Compiled entities (CustomClass, CustomField, etc.) still work normally.
        /// </summary>
        public static bool DegradedMode { get; private set; }

        /// <summary>
        /// Error message from the last failed bootstrap attempt.
        /// </summary>
        public static string DegradedModeReason { get; private set; }

        /// <summary>
        /// Tracks runtime types we've added to AdditionalExportedTypes,
        /// so we can remove stale ones when types are recompiled.
        /// </summary>
        private readonly HashSet<Type> _addedRuntimeTypes = new();

        public XafDynamicAssembliesModule()
        {
            Instance = this;
            //
            // XafDynamicAssembliesModule
            //
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.SystemModule.SystemModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.Chart.ChartModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.ConditionalAppearance.ConditionalAppearanceModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.Dashboards.DashboardsModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.Notifications.NotificationsModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.Office.OfficeModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.PivotGrid.PivotGridModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.ReportsV2.ReportsModuleV2));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.Scheduler.SchedulerModuleBase));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.TreeListEditors.TreeListEditorsModuleBase));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.Validation.ValidationModule));
            RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.ViewVariantsModule.ViewVariantsModule));
            AdditionalExportedTypes.Add(typeof(DevExpress.Persistent.BaseImpl.EF.FileData));
            AdditionalExportedTypes.Add(typeof(DevExpress.Persistent.BaseImpl.EF.FileAttachment));
            AdditionalExportedTypes.Add(typeof(DevExpress.Persistent.BaseImpl.EF.Event));
            AdditionalExportedTypes.Add(typeof(DevExpress.Persistent.BaseImpl.EF.Resource));
            AdditionalExportedTypes.Add(typeof(DevExpress.Persistent.BaseImpl.EF.HCategory));
            AdditionalExportedTypes.Add(typeof(BusinessObjects.CustomClass));
            AdditionalExportedTypes.Add(typeof(BusinessObjects.CustomField));
        }
        public override IEnumerable<ModuleUpdater> GetModuleUpdaters(IObjectSpace objectSpace, Version versionFromDB)
        {
            ModuleUpdater updater = new DatabaseUpdate.Updater(objectSpace, versionFromDB);
            return new ModuleUpdater[] { updater };
        }
        public override void Setup(XafApplication application)
        {
            base.Setup(application);
            CurrentApplication = application;

            if (!string.IsNullOrEmpty(RuntimeConnectionString))
            {
                BootstrapRuntimeEntities(application);
            }
        }
        public override void Setup(ApplicationModulesManager moduleManager)
        {
            base.Setup(moduleManager);
        }

        /// <summary>
        /// Compile runtime types early (before XAF initializes) so they're available
        /// for Web API endpoint registration in ConfigureServices.
        /// Sets RuntimeEntityTypes and ApiExposedClassNames.
        /// Safe to call multiple times — skips if already compiled.
        /// </summary>
        public static void EarlyBootstrap()
        {
            if (string.IsNullOrEmpty(RuntimeConnectionString)) return;

            var classes = QueryMetadata(RuntimeConnectionString);
            if (classes.Count == 0) return;

            // DDL sync (non-fatal)
            try
            {
                var schemaSyncer = new SchemaSynchronizer(RuntimeConnectionString);
                schemaSyncer.SynchronizeAll(classes);
            }
            catch (Exception ddlEx)
            {
                Tracing.Tracer.LogError($"[EarlyBootstrap] Schema synchronization failed: {ddlEx.Message}");
            }

            // Compile if not already loaded
            if (!AssemblyManager.HasLoadedAssembly || AssemblyManager.RuntimeTypes.Length == 0)
            {
                var result = AssemblyManager.LoadNewAssembly(classes);
                if (result.Success)
                {
                    XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes = result.RuntimeTypes;
                }
            }

            // Track API-exposed classes
            ApiExposedClassNames = new HashSet<string>(
                classes.Where(c => c.IsApiExposed).Select(c => c.ClassName));
        }

        private void BootstrapRuntimeEntities(XafApplication application)
        {
            DegradedMode = false;
            DegradedModeReason = null;

            try
            {
                var classes = QueryMetadata(RuntimeConnectionString);
                if (classes.Count == 0)
                {
                    Tracing.Tracer.LogText("No runtime entity metadata found. Skipping compilation.");
                    return;
                }

                Tracing.Tracer.LogText($"Found {classes.Count} runtime class(es). Synchronizing schema...");

                // DDL sync — safe even if compilation fails (extra columns are harmless)
                try
                {
                    var schemaSyncer = new SchemaSynchronizer(RuntimeConnectionString);
                    schemaSyncer.SynchronizeAll(classes);
                }
                catch (Exception ddlEx)
                {
                    Tracing.Tracer.LogError($"Schema synchronization failed: {ddlEx.Message}");
                    // DDL failure is non-fatal — compilation can still proceed
                    // for tables that already exist
                }

                Type[] runtimeTypes;
                if (AssemblyManager.HasLoadedAssembly && AssemblyManager.RuntimeTypes.Length > 0)
                {
                    runtimeTypes = AssemblyManager.RuntimeTypes;
                }
                else
                {
                    var result = AssemblyManager.LoadNewAssembly(classes);
                    if (!result.Success)
                    {
                        DegradedMode = true;
                        DegradedModeReason = $"Roslyn compilation failed: {string.Join("; ", result.Errors.Take(3))}";
                        Tracing.Tracer.LogError($"[DEGRADED MODE] {DegradedModeReason}");
                        return;
                    }
                    runtimeTypes = result.RuntimeTypes;
                }

                XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes = runtimeTypes;
                RefreshRuntimeTypes(runtimeTypes);

                // Populate API-exposed class names (in case EarlyBootstrap wasn't called)
                if (ApiExposedClassNames.Count == 0)
                {
                    ApiExposedClassNames = new HashSet<string>(
                        classes.Where(c => c.IsApiExposed).Select(c => c.ClassName));
                }

                // Seed the orchestrator so it knows the baseline type set
                SchemaChangeOrchestrator.Instance.SetKnownTypeNames(runtimeTypes.Select(t => t.Name));

                Tracing.Tracer.LogText($"Runtime entities bootstrapped: {string.Join(", ", runtimeTypes.Select(t => t.Name))}");
            }
            catch (Exception ex)
            {
                DegradedMode = true;
                DegradedModeReason = $"Bootstrap failed: {ex.Message}";
                Tracing.Tracer.LogError($"[DEGRADED MODE] {DegradedModeReason}");
            }
        }

        /// <summary>
        /// Update AdditionalExportedTypes with the given runtime types.
        /// Removes previously-added runtime types first to prevent duplicates
        /// across hot-load cycles or multiple Setup calls.
        /// </summary>
        public void RefreshRuntimeTypes(Type[] runtimeTypes)
        {
            // Remove previously-added runtime types
            foreach (var oldType in _addedRuntimeTypes)
            {
                AdditionalExportedTypes.Remove(oldType);
            }
            _addedRuntimeTypes.Clear();

            // Add current runtime types
            foreach (var type in runtimeTypes)
            {
                AdditionalExportedTypes.Add(type);
                _addedRuntimeTypes.Add(type);
            }
        }

        /// <summary>
        /// Query CustomClass and CustomField metadata directly via Npgsql.
        /// Returns empty list if tables don't exist yet (fresh database).
        /// </summary>
        internal static List<CustomClass> QueryMetadata(string connectionString)
        {
            var classes = new List<CustomClass>();

            using var conn = new NpgsqlConnection(connectionString);
            conn.Open();

            // Check if the CustomClasses table exists (fresh DB won't have it)
            using (var checkCmd = new NpgsqlCommand(
                "SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'CustomClasses')",
                conn))
            {
                if (!(bool)checkCmd.ExecuteScalar())
                    return classes;
            }

            // Check if IsApiExposed column exists (may not yet on first run after upgrade)
            bool hasApiExposedCol = false;
            using (var colCheck = new NpgsqlCommand(
                @"SELECT EXISTS (SELECT FROM information_schema.columns WHERE table_name = 'CustomClasses' AND column_name = 'IsApiExposed')",
                conn))
            {
                hasApiExposedCol = (bool)colCheck.ExecuteScalar();
            }

            // Query all runtime classes (Status = 'Runtime')
            var classMap = new Dictionary<Guid, CustomClass>();
            var selectSql = hasApiExposedCol
                ? @"SELECT ""ID"", ""ClassName"", ""NavigationGroup"", ""Description"", ""Status"", ""IsApiExposed""
                    FROM ""CustomClasses""
                    WHERE ""Status"" = 'Runtime' AND (""GCRecord"" IS NULL OR ""GCRecord"" = 0)"
                : @"SELECT ""ID"", ""ClassName"", ""NavigationGroup"", ""Description"", ""Status""
                    FROM ""CustomClasses""
                    WHERE ""Status"" = 'Runtime' AND (""GCRecord"" IS NULL OR ""GCRecord"" = 0)";
            using (var cmd = new NpgsqlCommand(selectSql, conn))
            {
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var cc = new CustomClass
                    {
                        ClassName = reader.GetString(1),
                        NavigationGroup = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                        IsApiExposed = hasApiExposedCol && !reader.IsDBNull(5) && reader.GetBoolean(5),
                    };
                    var id = reader.GetGuid(0);
                    classMap[id] = cc;
                    classes.Add(cc);
                }
            }

            if (classes.Count == 0)
                return classes;

            // Query all fields for the runtime classes
            var classIds = string.Join(",", classMap.Keys.Select(id => $"'{id}'"));
            using (var cmd = new NpgsqlCommand(
                $@"SELECT ""CustomClassId"", ""FieldName"", ""TypeName"", ""IsRequired"", ""IsDefaultField"",
                          ""Description"", ""ReferencedClassName"", ""SortOrder""
                   FROM ""CustomFields""
                   WHERE ""CustomClassId"" IN ({classIds}) AND (""GCRecord"" IS NULL OR ""GCRecord"" = 0)
                   ORDER BY ""SortOrder"", ""FieldName""",
                conn))
            {
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var classId = reader.GetGuid(0);
                    if (classMap.TryGetValue(classId, out var cc))
                    {
                        cc.Fields.Add(new CustomField
                        {
                            FieldName = reader.GetString(1),
                            TypeName = reader.IsDBNull(2) ? "System.String" : reader.GetString(2),
                            IsRequired = reader.GetBoolean(3),
                            IsDefaultField = reader.GetBoolean(4),
                            Description = reader.IsDBNull(5) ? null : reader.GetString(5),
                            ReferencedClassName = reader.IsDBNull(6) ? null : reader.GetString(6),
                            SortOrder = reader.GetInt32(7),
                        });
                    }
                }
            }

            return classes;
        }
    }
}
