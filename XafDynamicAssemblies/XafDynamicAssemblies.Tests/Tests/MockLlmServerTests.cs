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

        // TEST-002: keys must equal the real create_entity tool's C# parameter names —
        // the old snake_case shape (class_name/fields) made the tool silently fail
        // server-side in every mocked run while tests passed on canned follow-up text.
        var input = block.GetProperty("input");
        Assert.Equal("Customer", input.GetProperty("className").GetString());
        var fields = JsonSerializer.Deserialize<JsonElement>(input.GetProperty("fieldsJson").GetString()!);
        Assert.Equal(2, fields.GetArrayLength());
        Assert.Equal("Name", fields[0].GetProperty("name").GetString());
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

    [Fact]
    public async Task Anthropic_AddButton_Returns_ToolUse_CreateAction()
    {
        var body = new
        {
            model = "mock-model",
            messages = new object[] { new { role = "user", content = "add an 'Approve' button to 'Customer'" } },
        };
        var resp = await _client.PostAsJsonAsync("/v1/messages", body);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tool_use", json.GetProperty("stop_reason").GetString());
        var block = json.GetProperty("content")[0];
        Assert.Equal("create_action", block.GetProperty("name").GetString());

        var input = block.GetProperty("input");
        // Keys must equal the real tool's C# parameter names (see Global Constraints).
        Assert.Equal("Approve", input.GetProperty("caption").GetString());
        Assert.Equal("Customer", input.GetProperty("targetEntity").GetString());
        var steps = JsonSerializer.Deserialize<JsonElement>(input.GetProperty("stepsJson").GetString()!);
        Assert.Equal(2, steps.GetArrayLength());
        Assert.Equal("SetField", steps[0].GetProperty("kind").GetString());
        Assert.Equal("ShowMessage", steps[1].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Anthropic_DisableAction_Returns_SetActionActive_False()
    {
        var body = new
        {
            model = "mock-model",
            messages = new object[] { new { role = "user", content = "disable the 'Approve' action on 'Customer'" } },
        };
        var resp = await _client.PostAsJsonAsync("/v1/messages", body);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var block = json.GetProperty("content")[0];
        Assert.Equal("set_action_active", block.GetProperty("name").GetString());
        var input = block.GetProperty("input");
        Assert.Equal("Approve", input.GetProperty("caption").GetString());
        Assert.Equal("Customer", input.GetProperty("targetEntity").GetString());
        Assert.False(input.GetProperty("isActive").GetBoolean());
    }
}

/// <summary>
/// TEST-011: every tool_use input key the mock emits must be a real parameter of the C# tool
/// method it targets (AIFunctionFactory takes the schema keys from the parameter names). Ends
/// the create_entity (TEST-002) / describe_entity (TEST-011) drift class for good.
/// </summary>
public class MockToolContractTests
{
    private static readonly string[] SamplePrompts =
    {
        "list all entities", "list roles", "show the fields of 'Customer'", "what are the pending changes",
        "validate the schema", "create a 'Widget' entity", "yes",
        "list the actions on 'SchemaHistory'", "create an 'Approve' action on 'SchemaHistory'",
        "disable the 'Approve' action on 'SchemaHistory'", "delete the 'Approve' action on 'SchemaHistory'",
    };

    [Fact]
    public void Every_mock_tool_use_key_matches_a_real_tool_parameter()
    {
        var matcher = new ScriptMatcher();
        var seenTools = new HashSet<string>();
        foreach (var prompt in SamplePrompts)
        {
            var reply = matcher.Match(prompt);
            if ((string)reply["type"] != "tool_use") continue;
            var tool = (string)reply["name"];
            seenTools.Add(tool);
            var methodName = string.Concat(tool.Split('_').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
            var method = typeof(XafDynamicAssemblies.Module.Services.SchemaAIToolsProvider)
                .GetMethod(methodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.True(method != null, $"mock targets tool '{tool}' but SchemaAIToolsProvider has no method '{methodName}'");
            var parameters = method!.GetParameters().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var key in ((Dictionary<string, object>)reply["input"]).Keys)
                Assert.True(parameters.Contains(key), $"mock sends '{key}' to {tool}, real parameters: {string.Join(", ", parameters)}");
        }
        Assert.Contains("describe_entity", seenTools);
        Assert.Contains("create_entity", seenTools);
    }
}
