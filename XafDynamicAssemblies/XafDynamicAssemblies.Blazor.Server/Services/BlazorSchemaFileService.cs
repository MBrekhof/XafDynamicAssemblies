using Microsoft.JSInterop;
using XafDynamicAssemblies.Module.Services;

namespace XafDynamicAssemblies.Blazor.Server.Services
{
    public class BlazorSchemaFileService : ISchemaFileService
    {
        private readonly IJSRuntime _jsRuntime;

        public BlazorSchemaFileService(IJSRuntime jsRuntime)
        {
            _jsRuntime = jsRuntime;
        }

        public async Task DownloadJsonAsync(string fileName, string jsonContent)
        {
            await _jsRuntime.InvokeVoidAsync("schemaFile.download", fileName, jsonContent);
        }

        public async Task<string> UploadJsonAsync()
        {
            return await _jsRuntime.InvokeAsync<string>("schemaFile.upload", ".json");
        }
    }
}
