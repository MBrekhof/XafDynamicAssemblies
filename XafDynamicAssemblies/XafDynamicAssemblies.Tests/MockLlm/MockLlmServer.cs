using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace XafDynamicAssemblies.Tests.MockLlm;

/// <summary>
/// Mock LLM server for deterministic testing. Ported from tests/mock_llm/server.py (Flask).
/// Supports both the Anthropic Messages API (/v1/messages) and the OpenAI Chat Completions
/// API (/v1/chat/completions) wire formats, driven by <see cref="ScriptMatcher"/>.
/// </summary>
public class MockLlmServer
{
    private readonly WebApplication _app;
    private readonly ScriptMatcher _matcher = new();
    private int _idCounter;

    public MockLlmServer(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        _app.MapGet("/health", () => Results.Json(new { status = "ok" }));
        // Python's reset_state() only clears pending-entity state; the id counter is not reset.
        _app.MapPost("/reset", () => { _matcher.Reset(); return Results.Json(new { status = "reset" }); });
        // Typed Func<> locals (not bare method groups) so RequestDelegateFactory sees the real
        // Task<IResult> return type via reflection instead of silently treating it as Task and
        // discarding the result (ASP0016).
        Func<HttpContext, Task<IResult>> anthropicHandler = HandleAnthropicAsync;
        Func<HttpContext, Task<IResult>> openAiHandler = HandleOpenAIAsync;
        _app.MapPost("/v1/messages", anthropicHandler);
        _app.MapPost("/v1/chat/completions", openAiHandler);
    }

    public Task StartAsync() => _app.StartAsync();
    public Task StopAsync() => _app.StopAsync();

    private async Task<IResult> HandleAnthropicAsync(HttpContext ctx)
    {
        using var body = await JsonDocument.ParseAsync(ctx.Request.Body);

        var toolName = HasToolResultAnthropic(body);
        if (!string.IsNullOrEmpty(toolName))
        {
            var followup = _matcher.MatchToolResult(toolName);
            return Results.Json(AnthropicText((string)followup["text"]));
        }

        var userMsg = ExtractLastUserMessageAnthropic(body);
        if (string.IsNullOrEmpty(userMsg))
            return Results.Json(AnthropicText("I didn't catch that. Could you rephrase?"));

        var result = _matcher.Match(userMsg);
        return (string)result["type"] == "tool_use"
            ? Results.Json(AnthropicTool((string)result["name"], result["input"]))
            : Results.Json(AnthropicText((string)result["text"]));
    }

    private async Task<IResult> HandleOpenAIAsync(HttpContext ctx)
    {
        using var body = await JsonDocument.ParseAsync(ctx.Request.Body);

        var toolName = HasToolResultOpenAI(body);
        if (!string.IsNullOrEmpty(toolName))
        {
            var followup = _matcher.MatchToolResult(toolName);
            return Results.Json(OpenAiText((string)followup["text"]));
        }

        var userMsg = ExtractLastUserMessageOpenAI(body);
        if (string.IsNullOrEmpty(userMsg))
            return Results.Json(OpenAiText("I didn't catch that. Could you rephrase?"));

        var result = _matcher.Match(userMsg);
        return (string)result["type"] == "tool_use"
            ? Results.Json(OpenAiTool((string)result["name"], result["input"]))
            : Results.Json(OpenAiText((string)result["text"]));
    }

    // -----------------------------------------------------------------
    // Anthropic Messages format response builders (server.py _anthropic_*)
    // -----------------------------------------------------------------

    private object AnthropicText(string text) => new
    {
        id = NextId(),
        type = "message",
        role = "assistant",
        model = "mock-model",
        content = new object[] { new { type = "text", text } },
        stop_reason = "end_turn",
        stop_sequence = (string?)null,
        usage = new { input_tokens = 10, output_tokens = text.Length / 4 },
    };

    private object AnthropicTool(string name, object input) => new
    {
        id = NextId(),
        type = "message",
        role = "assistant",
        model = "mock-model",
        content = new object[] { new { type = "tool_use", id = ToolCallId(), name, input } },
        stop_reason = "tool_use",
        stop_sequence = (string?)null,
        usage = new { input_tokens = 10, output_tokens = 20 },
    };

    // -----------------------------------------------------------------
    // OpenAI Chat Completions format response builders (server.py _openai_*)
    // -----------------------------------------------------------------

    private object OpenAiText(string text) => new
    {
        id = NextId(),
        @object = "chat.completion",
        model = "mock-model",
        choices = new object[]
        {
            new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" },
        },
        usage = new { prompt_tokens = 10, completion_tokens = text.Length / 4, total_tokens = 10 + text.Length / 4 },
    };

    private object OpenAiTool(string name, object input) => new
    {
        id = NextId(),
        @object = "chat.completion",
        model = "mock-model",
        choices = new object[]
        {
            new
            {
                index = 0,
                message = new
                {
                    role = "assistant",
                    content = (string?)null,
                    tool_calls = new object[]
                    {
                        new
                        {
                            id = ToolCallId(),
                            type = "function",
                            function = new { name, arguments = JsonSerializer.Serialize(input) },
                        },
                    },
                },
                finish_reason = "tool_calls",
            },
        },
        usage = new { prompt_tokens = 10, completion_tokens = 20, total_tokens = 30 },
    };

    private string NextId() => $"mock-{++_idCounter}";

    private static string ToolCallId() => $"call_{Guid.NewGuid():N}"[..17]; // "call_" + 12 hex chars, matches Python's uuid4().hex[:12]

    // -----------------------------------------------------------------
    // Message extraction — ported field-for-field from server.py so multi-turn
    // tool_result / tool_call follow-up flows behave identically.
    // -----------------------------------------------------------------

    private static string? ExtractLastUserMessageAnthropic(JsonDocument body)
    {
        if (!body.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return null;

        for (var i = messages.GetArrayLength() - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (!msg.TryGetProperty("role", out var roleEl) || roleEl.GetString() != "user")
                continue;

            if (!msg.TryGetProperty("content", out var content))
                return ""; // Python: body.get("content", "") defaults to "" (falsy)

            if (content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? "";

            if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind == JsonValueKind.String)
                        return block.GetString() ?? "";
                    if (block.ValueKind != JsonValueKind.Object) continue;

                    var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "text")
                        return block.TryGetProperty("text", out var txt) ? (txt.GetString() ?? "") : "";
                    if (type == "tool_result")
                        return null;
                }
                // No block matched: Python doesn't break here — keeps scanning earlier messages.
            }
        }
        return null;
    }

    private static string? HasToolResultAnthropic(JsonDocument body)
    {
        if (!body.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return null;

        for (var i = messages.GetArrayLength() - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (!msg.TryGetProperty("role", out var roleEl) || roleEl.GetString() != "user")
                continue;

            if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    if (!block.TryGetProperty("type", out var t) || t.GetString() != "tool_result") continue;

                    var toolUseId = block.TryGetProperty("tool_use_id", out var idEl) ? idEl.GetString() : null;
                    for (var j = messages.GetArrayLength() - 1; j >= 0; j--)
                    {
                        var prevMsg = messages[j];
                        if (!prevMsg.TryGetProperty("role", out var prevRole) || prevRole.GetString() != "assistant")
                            continue;
                        if (!prevMsg.TryGetProperty("content", out var prevContent) || prevContent.ValueKind != JsonValueKind.Array)
                            continue;
                        foreach (var prevBlock in prevContent.EnumerateArray())
                        {
                            if (prevBlock.ValueKind != JsonValueKind.Object) continue;
                            var prevType = prevBlock.TryGetProperty("type", out var pt) ? pt.GetString() : null;
                            var prevId = prevBlock.TryGetProperty("id", out var pid) ? pid.GetString() : null;
                            if (prevType == "tool_use" && prevId == toolUseId)
                                return prevBlock.TryGetProperty("name", out var n) ? n.GetString() : null;
                        }
                    }
                    return "unknown_tool";
                }
            }
            break; // Python only inspects the last user-role message.
        }
        return null;
    }

    private static string? ExtractLastUserMessageOpenAI(JsonDocument body)
    {
        if (!body.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return null;

        for (var i = messages.GetArrayLength() - 1; i >= 0; i--)
        {
            var msg = messages[i];
            var role = msg.TryGetProperty("role", out var roleEl) ? roleEl.GetString() : null;
            if (role == "user")
                return msg.TryGetProperty("content", out var c) ? (c.GetString() ?? "") : "";
            if (role == "tool")
                return null;
        }
        return null;
    }

    private static string? HasToolResultOpenAI(JsonDocument body)
    {
        if (!body.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return null;

        for (var i = messages.GetArrayLength() - 1; i >= 0; i--)
        {
            var msg = messages[i];
            var role = msg.TryGetProperty("role", out var roleEl) ? roleEl.GetString() : null;

            if (role == "tool")
            {
                var toolCallId = msg.TryGetProperty("tool_call_id", out var idEl) ? idEl.GetString() : null;
                for (var j = messages.GetArrayLength() - 1; j >= 0; j--)
                {
                    var prevMsg = messages[j];
                    if (!prevMsg.TryGetProperty("role", out var prevRole) || prevRole.GetString() != "assistant")
                        continue;
                    if (!prevMsg.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        var tcId = tc.TryGetProperty("id", out var tid) ? tid.GetString() : null;
                        if (tcId == toolCallId)
                            return tc.GetProperty("function").GetProperty("name").GetString();
                    }
                }
                return "unknown_tool";
            }

            if (role is "user" or "assistant")
                break;
        }
        return null;
    }
}
