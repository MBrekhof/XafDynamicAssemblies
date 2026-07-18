using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using XafDynamicAssemblies.Tests.MockLlm;

namespace XafDynamicAssemblies.Tests.Tests;

/// <summary>
/// In-process smoke tests for MockLlmServer — no browser/XAF app dependency, so each test
/// starts its own server on a free port and disposes it. Verifies wire-format fidelity against
/// tests/mock_llm/server.py + scripts.py (see ScriptMatcher.cs / MockLlmServer.cs deviation notes).
/// </summary>
public class MockLlmServerTests : IAsyncLifetime
{
    private MockLlmServer _server = null!;
    private readonly HttpClient _client = new();
    private readonly int _port = GetFreePort();

    public async Task InitializeAsync()
    {
        _server = new MockLlmServer(_port);
        await _server.StartAsync();
        _client.BaseAddress = new Uri($"http://localhost:{_port}");
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        _client.Dispose();
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Health_Responds_Ok()
    {
        var resp = await _client.GetAsync("/health");
        Assert.True(resp.IsSuccessStatusCode);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Anthropic_CreateEntity_Returns_ScriptedText()
    {
        var body = new
        {
            model = "mock-model",
            messages = new object[] { new { role = "user", content = "create a Customer entity" } },
        };

        var resp = await _client.PostAsJsonAsync("/v1/messages", body);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("end_turn", json.GetProperty("stop_reason").GetString());
        var text = json.GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("Customer", text);
        Assert.Contains("Look good?", text);
    }

    [Fact]
    public async Task Anthropic_Confirm_Returns_ToolUse_CreateEntity()
    {
        var proposeBody = new
        {
            model = "mock-model",
            messages = new object[] { new { role = "user", content = "create a Customer entity" } },
        };
        await _client.PostAsJsonAsync("/v1/messages", proposeBody);

        var confirmBody = new
        {
            model = "mock-model",
            messages = new object[] { new { role = "user", content = "yes" } },
        };
        var resp = await _client.PostAsJsonAsync("/v1/messages", confirmBody);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tool_use", json.GetProperty("stop_reason").GetString());

        var block = json.GetProperty("content")[0];
        Assert.Equal("tool_use", block.GetProperty("type").GetString());
        Assert.Equal("create_entity", block.GetProperty("name").GetString());
        Assert.StartsWith("call_", block.GetProperty("id").GetString());

        var input = block.GetProperty("input");
        Assert.Equal("Customer", input.GetProperty("class_name").GetString());
        Assert.Equal(2, input.GetProperty("fields").GetArrayLength());
    }

    [Fact]
    public async Task Reset_Clears_Pending_Entity()
    {
        var proposeBody = new
        {
            model = "mock-model",
            messages = new object[] { new { role = "user", content = "create a Customer entity" } },
        };
        await _client.PostAsJsonAsync("/v1/messages", proposeBody);

        var resetResp = await _client.PostAsync("/reset", null);
        Assert.True(resetResp.IsSuccessStatusCode);

        var confirmBody = new
        {
            model = "mock-model",
            messages = new object[] { new { role = "user", content = "yes" } },
        };
        var resp = await _client.PostAsJsonAsync("/v1/messages", confirmBody);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // No pending entity after reset -> generic confirm text, not a tool_use.
        Assert.Equal("end_turn", json.GetProperty("stop_reason").GetString());
        Assert.Equal("OK, confirmed.", json.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task OpenAI_Format_Returns_OpenAI_WireShape()
    {
        var body = new
        {
            model = "mock-model",
            messages = new object[] { new { role = "user", content = "list entities" } },
        };

        var resp = await _client.PostAsJsonAsync("/v1/chat/completions", body);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("chat.completion", json.GetProperty("object").GetString());

        var choice = json.GetProperty("choices")[0];
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());

        var toolCall = choice.GetProperty("message").GetProperty("tool_calls")[0];
        Assert.Equal("function", toolCall.GetProperty("type").GetString());
        Assert.Equal("list_entities", toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{}", toolCall.GetProperty("function").GetProperty("arguments").GetString());
    }
}
