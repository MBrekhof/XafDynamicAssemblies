using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using XafDynamicAssemblies.Module.BusinessObjects;

namespace XafDynamicAssemblies.Module.Services
{
    /// <summary>
    /// Coordinates hot-load of runtime entities.
    /// SemaphoreSlim-guarded sequence: DDL → Roslyn → update DbContext.RuntimeEntityTypes →
    /// register in TypesInfo → notify.
    /// RestartNeeded is always set after any successful compilation because XAF's
    /// process-static TypesInfo and SharedApplicationModelManagerContainer cannot be
    /// properly reset in-process.
    /// </summary>
    public class SchemaChangeOrchestrator
    {
        private static readonly Lazy<SchemaChangeOrchestrator> _instance = new(() => new());
        private static readonly SemaphoreSlim _semaphore = new(1, 1);
        private int _schemaVersion;
        private HashSet<string> _previousTypeNames = new();

        public static SchemaChangeOrchestrator Instance => _instance.Value;

        /// <summary>Fired after successful hot-load with the new schema version.</summary>
        public event Action<int> SchemaChanged;

        public int SchemaVersion => _schemaVersion;

        /// <summary>
        /// Always true after any successful compilation. XAF's process-static TypesInfo
        /// cannot be reset in-process, so every recompilation requires a process restart.
        /// </summary>
        public bool RestartNeeded { get; private set; }

        /// <summary>
        /// Call after bootstrap to seed the known type names.
        /// This prevents false RestartNeeded on the first hot-load after startup.
        /// </summary>
        public void SetKnownTypeNames(IEnumerable<string> typeNames)
        {
            _previousTypeNames = new HashSet<string>(typeNames);
        }

        public async Task ExecuteHotLoadAsync()
        {
            if (!await _semaphore.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                Tracing.Tracer.LogError("Hot-load timed out waiting for semaphore.");
                return;
            }

            try
            {
                var connStr = XafDynamicAssembliesModule.RuntimeConnectionString;
                if (string.IsNullOrEmpty(connStr))
                    return;

                // 1. Query current metadata
                var classes = XafDynamicAssembliesModule.QueryMetadata(connStr);

                // 2. Synchronize DDL (safe even if compilation fails — extra columns are harmless)
                try
                {
                    var syncer = new SchemaSynchronizer(connStr);
                    syncer.SynchronizeAll(classes);
                }
                catch (Exception ddlEx)
                {
                    Tracing.Tracer.LogError($"DDL sync failed (non-fatal): {ddlEx.Message}");
                    // Continue — compilation may still succeed for existing tables
                }

                if (classes.Count == 0)
                {
                    var hadTypes = _previousTypeNames.Count > 0;
                    _previousTypeNames.Clear();
                    XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes = Array.Empty<Type>();
                    RestartNeeded = hadTypes;
                    var ver = Interlocked.Increment(ref _schemaVersion);
                    SchemaChanged?.Invoke(ver);
                    return;
                }

                // 3. Compile via Roslyn
                var result = XafDynamicAssembliesModule.AssemblyManager.LoadNewAssembly(classes);
                if (!result.Success)
                {
                    Tracing.Tracer.LogError("Hot-load compilation failed:");
                    foreach (var error in result.Errors)
                        Tracing.Tracer.LogError("  " + error);
                    // Still trigger restart so the server re-enters degraded mode cleanly
                    RestartNeeded = true;
                    var ver = Interlocked.Increment(ref _schemaVersion);
                    SchemaChanged?.Invoke(ver);
                    return;
                }

                // 4. Update DbContext types (atomic reference swap)
                XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes = result.RuntimeTypes;

                // 5. Register types with XAF's TypesInfo
                RegisterTypesInTypesInfo(result.RuntimeTypes);

                // 6. Update module's AdditionalExportedTypes
                XafDynamicAssembliesModule.Instance?.RefreshRuntimeTypes(result.RuntimeTypes);

                // 7. Always restart after compilation — XAF's process-static TypesInfo
                // and SharedApplicationModelManagerContainer cannot be properly reset
                // in-process, so any recompilation requires a fresh process.
                var newTypeNames = new HashSet<string>(result.RuntimeTypes.Select(t => t.Name));
                RestartNeeded = true;
                _previousTypeNames = newTypeNames;

                // 8. Notify (Startup.cs wires this to SignalR broadcast + conditional restart)
                var version = Interlocked.Increment(ref _schemaVersion);
                SchemaChanged?.Invoke(version);
            }
            catch (Exception ex)
            {
                Tracing.Tracer.LogError($"Hot-load failed: {ex.Message}");
                // Trigger restart to recover cleanly
                RestartNeeded = true;
                SchemaChanged?.Invoke(Interlocked.Increment(ref _schemaVersion));
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private static void RegisterTypesInTypesInfo(Type[] runtimeTypes)
        {
            foreach (var type in runtimeTypes)
            {
                try
                {
                    XafTypesInfo.Instance.RegisterEntity(type);
                }
                catch (Exception ex)
                {
                    Tracing.Tracer.LogError($"TypesInfo registration failed for {type.Name}: {ex.Message}");
                }
            }
        }

    }
}
