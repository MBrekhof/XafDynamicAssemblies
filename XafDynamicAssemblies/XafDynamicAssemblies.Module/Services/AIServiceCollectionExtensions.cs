using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace XafDynamicAssemblies.Module.Services;

public static class AIServiceCollectionExtensions
{
    public static IServiceCollection AddAIServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Bind AI config section
        services.Configure<AIOptions>(configuration.GetSection("AI"));

        // Singleton: shared TornadoApi instance and schema discovery
        services.AddSingleton<TornadoApiProvider>();
        services.AddSingleton<SchemaDiscoveryService>();
        services.AddSingleton<SchemaAIToolsProvider>();

        // Scoped: per-Blazor-circuit chat session with independent history
        services.AddScoped<AIChatService>();

        // IChatClient adapter — scoped (one per circuit, wraps the scoped AIChatService)
        services.AddScoped<IChatClient>(sp =>
        {
            var chatService = sp.GetRequiredService<AIChatService>();
            var tools = sp.GetRequiredService<SchemaAIToolsProvider>();

            // Wire tools — both AIFunction (for execution) and LLMTornado Tool (for schema)
            chatService.ToolFunctions = tools.Tools;
            chatService.TornadoTools = tools.GetTornadoTools();

            // System prompt is refreshed automatically in AskAsync() with current metadata
            return new AIChatClient(chatService);
        });

        return services;
    }
}
