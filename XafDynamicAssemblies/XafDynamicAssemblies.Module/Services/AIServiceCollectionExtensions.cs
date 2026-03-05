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

        // Core services
        services.AddSingleton<SchemaDiscoveryService>();
        services.AddSingleton<AIChatService>();
        services.AddSingleton<SchemaAIToolsProvider>();

        // IChatClient adapter for DevExpress DxAIChat integration
        services.AddChatClient(sp =>
        {
            var chatService = sp.GetRequiredService<AIChatService>();
            var tools = sp.GetRequiredService<SchemaAIToolsProvider>();
            var discovery = sp.GetRequiredService<SchemaDiscoveryService>();

            // Wire tools — both AIFunction (for execution) and LLMTornado Tool (for schema)
            chatService.ToolFunctions = tools.Tools;
            chatService.TornadoTools = tools.GetTornadoTools();

            // Set initial system prompt (empty runtime entity list — refreshed at chat time)
            chatService.SystemMessage = discovery.GenerateSystemPrompt(new List<CustomClassSummary>());

            return new AIChatClient(chatService);
        });

        return services;
    }
}
