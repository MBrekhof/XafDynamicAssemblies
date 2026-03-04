using Microsoft.Extensions.Hosting;

namespace XafDynamicAssemblies.Blazor.Server.Services
{
    /// <summary>
    /// Allows the application to request a graceful restart (e.g. after hot-load).
    /// Program.cs runs the host in a loop and checks IsRestartRequested after shutdown.
    /// </summary>
    public static class RestartService
    {
        private static IHostApplicationLifetime _lifetime;
        private static volatile bool _restartRequested;

        public static bool IsRestartRequested => _restartRequested;

        public static void Configure(IHostApplicationLifetime lifetime)
        {
            _lifetime = lifetime;
        }

        /// <summary>
        /// Signal the host to stop and restart.
        /// </summary>
        public static void RequestRestart()
        {
            _restartRequested = true;
            _lifetime?.StopApplication();
        }

        public static void ResetRestartFlag()
        {
            _restartRequested = false;
        }
    }
}
