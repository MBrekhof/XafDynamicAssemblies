using DevExpress.ExpressApp;
using DevExpress.Persistent.Base;
using XafDynamicAssemblies.Module.BusinessObjects;

namespace XafDynamicAssemblies.Module.Services
{
    /// <summary>
    /// Coordinates hot-load of runtime entities.
    /// SemaphoreSlim-guarded sequence: DDL → Roslyn (validates metadata) → notify → restart.
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

        /// <summary>
        /// Runs DDL sync, compile and restart scheduling. Returns null on success, otherwise the
        /// error to show the user. A DDL failure stops the deploy: nothing is compiled and no restart
        /// is scheduled, because a restarted process would carry an EF model whose columns the
        /// table lacks (DATA-003).
        /// </summary>
        public async Task<string> ExecuteHotLoadAsync()
        {
            if (!await _semaphore.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                Tracing.Tracer.LogError("Hot-load timed out waiting for semaphore.");
                return "Deploy is already running; try again in a moment.";
            }

            try
            {
                var connStr = XafDynamicAssembliesModule.RuntimeConnectionString;
                if (string.IsNullOrEmpty(connStr))
                    return "No runtime connection string configured.";

                // 1. Query current metadata
                var classes = XafDynamicAssembliesModule.QueryMetadata(connStr);

                // 2. Synchronize DDL — a failure aborts the deploy (see summary)
                try
                {
                    var syncer = new SchemaSynchronizer(connStr);
                    syncer.SynchronizeAll(classes);
                }
                catch (Exception ddlEx)
                {
                    Tracing.Tracer.LogError($"Deploy aborted, DDL sync failed: {ddlEx.Message}");
                    return $"Deploy aborted, schema synchronization failed: {ddlEx.Message}";
                }

                if (classes.Count == 0)
                {
                    var hadTypes = _previousTypeNames.Count > 0;
                    _previousTypeNames.Clear();
                    RestartNeeded = hadTypes;
                    var ver = Interlocked.Increment(ref _schemaVersion);
                    SchemaChanged?.Invoke(ver);
                    return null;
                }

                // 3. Compile via Roslyn — validation only (HOT-001): nothing is loaded into this
                // process, so AssemblyManager/DbContext keep the types the open views hold.
                var result = RuntimeAssemblyBuilder.ValidateCompilation(classes);
                if (!result.Success)
                {
                    Tracing.Tracer.LogError("Deploy aborted, compilation failed:");
                    foreach (var error in result.Errors)
                        Tracing.Tracer.LogError("  " + error);
                    // This process still runs the previous, working type set (HOT-001), so there
                    // is nothing to recover from by restarting; a restart would only boot into
                    // degraded mode. Surface the errors and keep serving.
                    return "Deploy aborted, compilation failed: " + string.Join("; ", result.Errors.Take(3));
                }

                // HOT-001: no in-process type swap. The compile above only validates the metadata;
                // publishing new Type identities into the dying process (DbContext types, TypesInfo,
                // AdditionalExportedTypes) made requests in the ~3 s restart window fail with
                // "not part of the model" while open views still held the old types. The new
                // process recompiles from metadata in EarlyBootstrap.

                // 4. Always restart after compilation — XAF's process-static TypesInfo
                // and SharedApplicationModelManagerContainer cannot be properly reset
                // in-process, so any recompilation requires a fresh process.
                var newTypeNames = new HashSet<string>(classes.Select(c => c.ClassName));
                RestartNeeded = true;
                _previousTypeNames = newTypeNames;

                // 5. Notify (Startup.cs wires this to SignalR broadcast + conditional restart)
                var version = Interlocked.Increment(ref _schemaVersion);
                SchemaChanged?.Invoke(version);
                return null;
            }
            catch (Exception ex)
            {
                Tracing.Tracer.LogError($"Hot-load failed: {ex.Message}");
                // Trigger restart to recover cleanly
                RestartNeeded = true;
                SchemaChanged?.Invoke(Interlocked.Increment(ref _schemaVersion));
                return "Deploy failed: " + ex.Message;
            }
            finally
            {
                _semaphore.Release();
            }
        }

    }
}
