using Microsoft.AspNetCore.SignalR;

namespace XafDynamicAssemblies.Blazor.Server.Hubs
{
    /// <summary>
    /// SignalR hub for broadcasting schema version changes to connected Blazor clients.
    /// Clients receive "SchemaChanged" and reload to pick up new runtime entities.
    /// </summary>
    public class SchemaUpdateHub : Hub
    {
    }
}
