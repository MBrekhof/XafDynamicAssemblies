using DevExpress.ExpressApp.ApplicationBuilder;
using DevExpress.ExpressApp.Blazor.ApplicationBuilder;
using DevExpress.ExpressApp.Blazor.Services;
using DevExpress.Persistent.Base;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using XafDynamicAssemblies.Blazor.Server.Hubs;
using XafDynamicAssemblies.Blazor.Server.Services;
using XafDynamicAssemblies.Module.Services;

namespace XafDynamicAssemblies.Blazor.Server
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton(typeof(Microsoft.AspNetCore.SignalR.HubConnectionHandler<>), typeof(ProxyHubConnectionHandler<>));

            services.AddRazorPages();
            services.AddServerSideBlazor();
            services.AddHttpContextAccessor();
            services.AddScoped<CircuitHandler, CircuitHandlerProxy>();
            // Set connection string for runtime entity bootstrap (before XAF initializes)
            XafDynamicAssemblies.Module.XafDynamicAssembliesModule.RuntimeConnectionString =
                Configuration.GetConnectionString("ConnectionString");

            services.AddXaf(Configuration, builder =>
            {
                builder.UseApplication<XafDynamicAssembliesBlazorApplication>();
                builder.Modules
                    .AddConditionalAppearance()
                    .AddDashboards(options =>
                    {
                        options.DashboardDataType = typeof(DevExpress.Persistent.BaseImpl.EF.DashboardData);
                    })
                    .AddFileAttachments()
                    .AddNotifications()
                    .AddOffice()
                    .AddReports(options =>
                    {
                        options.EnableInplaceReports = true;
                        options.ReportDataType = typeof(DevExpress.Persistent.BaseImpl.EF.ReportDataV2);
                        options.ReportStoreMode = DevExpress.ExpressApp.ReportsV2.ReportStoreModes.XML;
                    })
                    .AddScheduler()
                    .AddValidation(options =>
                    {
                        options.AllowValidationDetailsAccess = false;
                    })
                    .AddViewVariants()
                    .Add<XafDynamicAssemblies.Module.XafDynamicAssembliesModule>()
                    .Add<XafDynamicAssembliesBlazorModule>();
                builder.ObjectSpaceProviders
                    .AddEFCore(options =>
                    {
                        options.PreFetchReferenceProperties();
                    })
                    .WithDbContext<XafDynamicAssemblies.Module.BusinessObjects.XafDynamicAssembliesEFCoreDbContext>((serviceProvider, options) =>
                    {
                        // Uncomment this code to use an in-memory database. This database is recreated each time the server starts. With the in-memory database, you don't need to make a migration when the data model is changed.
                        // Do not use this code in production environment to avoid data loss.
                        // We recommend that you refer to the following help topic before you use an in-memory database: https://docs.microsoft.com/en-us/ef/core/testing/in-memory
                        //options.UseInMemoryDatabase();
                        string connectionString = null;
                        if (Configuration.GetConnectionString("ConnectionString") != null)
                        {
                            connectionString = Configuration.GetConnectionString("ConnectionString");
                        }
#if EASYTEST
                        if(Configuration.GetConnectionString("EasyTestConnectionString") != null) {
                            connectionString = Configuration.GetConnectionString("EasyTestConnectionString");
                        }
#endif
                        ArgumentNullException.ThrowIfNull(connectionString);
                        options.UseNpgsql(connectionString);
                        options.ReplaceService<IModelCacheKeyFactory, DynamicModelCacheKeyFactory>();
                        options.UseChangeTrackingProxies();
                        options.UseLazyLoadingProxies();
                    })
                    .AddNonPersistent();
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. To change this for production scenarios, see: https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }
            app.UseHttpsRedirection();
            app.UseRequestLocalization();
            app.UseStaticFiles();
            app.UseRouting();
            app.UseXaf();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapXafEndpoints();
                endpoints.MapBlazorHub();
                endpoints.MapHub<SchemaUpdateHub>("/schemaUpdateHub");
                endpoints.MapFallbackToPage("/_Host");
                endpoints.MapControllers();
            });

            // Allow RestartService to trigger graceful shutdown for hot-load restart
            var lifetime = app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>();
            RestartService.Configure(lifetime);

            // Wire schema change orchestrator to SignalR hub for client notifications
            // and schedule a graceful restart when the type set changes (new/removed classes)
            var hubContext = app.ApplicationServices.GetRequiredService<IHubContext<SchemaUpdateHub>>();
            var orchestrator = XafDynamicAssemblies.Module.Services.SchemaChangeOrchestrator.Instance;
            orchestrator.SchemaChanged += (version) =>
            {
                var needsRestart = orchestrator.RestartNeeded;

                // Notify clients with restart flag so they know whether to poll for reconnection
                _ = hubContext.Clients.All.SendAsync("SchemaChanged", version, needsRestart);

                // Only restart if the set of type names changed (new/removed classes).
                // Field-only changes are handled in-memory without restart.
                if (needsRestart)
                {
                    _ = Task.Run(async () =>
                    {
                        // Give SignalR time to deliver the SchemaChanged message to clients
                        await Task.Delay(3000);
                        // Force-exit the process. Graceful shutdown via StopApplication()
                        // can hang indefinitely due to active Blazor SignalR connections.
                        // run-server.bat detects exit code 42 and restarts the process.
                        Console.WriteLine("[RESTART] Force-exiting for restart (exit code 42)...");
                        Environment.Exit(42);
                    });
                }
            };
        }
    }
}
