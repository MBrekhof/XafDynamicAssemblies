using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using XafDynamicAssemblies.Module.BusinessObjects;

namespace XafDynamicAssemblies.Module.Services
{
    /// <summary>
    /// Manages the lifecycle of runtime-compiled assemblies.
    /// Handles compilation, loading into collectible ALCs, and unloading old versions.
    /// </summary>
    public class AssemblyGenerationManager
    {
        private readonly ILogger _logger;
        private CompilationResult _currentResult;
        private readonly object _lock = new();

        public AssemblyGenerationManager(ILogger logger = null)
        {
            _logger = logger;
        }

        /// <summary>
        /// Current runtime entity types from the most recent successful compilation.
        /// </summary>
        public Type[] RuntimeTypes => _currentResult?.RuntimeTypes ?? Array.Empty<Type>();

        /// <summary>
        /// Generated source code from the most recent compilation, keyed by class name.
        /// </summary>
        public IReadOnlyDictionary<string, string> GeneratedSources =>
            _currentResult?.GeneratedSources ?? new Dictionary<string, string>();

        /// <summary>
        /// Whether a runtime assembly is currently loaded.
        /// </summary>
        public bool HasLoadedAssembly => _currentResult?.Assembly != null;

        /// <summary>
        /// Compile metadata into a new assembly and replace the current one.
        /// Returns the compilation result.
        /// </summary>
        public CompilationResult LoadNewAssembly(List<CustomClass> classes)
        {
            lock (_lock)
            {
                _logger?.LogInformation("Compiling {Count} runtime classes...", classes.Count);

                var result = RuntimeAssemblyBuilder.Compile(classes);

                if (result.Success)
                {
                    _logger?.LogInformation(
                        "Compilation succeeded. {TypeCount} types generated.",
                        result.RuntimeTypes.Length);

                    foreach (var warning in result.Warnings)
                        _logger?.LogWarning("Roslyn warning: {Warning}", warning);

                    // Unload old ALC
                    UnloadCurrent();

                    _currentResult = result;
                }
                else
                {
                    _logger?.LogError("Compilation failed with {ErrorCount} errors:", result.Errors.Count);
                    foreach (var error in result.Errors)
                        _logger?.LogError("  {Error}", error);
                }

                return result;
            }
        }

        /// <summary>
        /// Unload the current assembly and its ALC.
        /// </summary>
        public void UnloadCurrent()
        {
            lock (_lock)
            {
                if (_currentResult?.LoadContext != null)
                {
                    _logger?.LogInformation("Unloading previous runtime assembly...");

                    if (_currentResult.LoadContext.IsCollectible)
                    {
                        var weakRef = new WeakReference(_currentResult.LoadContext);
                        _currentResult.LoadContext.Unload();
                        _currentResult = null;

                        // Encourage GC to collect the unloaded ALC
                        for (int i = 0; i < 5 && weakRef.IsAlive; i++)
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                        }
                    }
                    else
                    {
                        // Non-collectible ALC: just drop the reference
                        _currentResult = null;
                    }
                }
            }
        }
    }
}
