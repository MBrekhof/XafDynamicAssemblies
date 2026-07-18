using XafDynamicAssemblies.Tests.Fixtures;
using XafDynamicAssemblies.Tests.Pages;
using Xunit.Abstractions;

namespace XafDynamicAssemblies.Tests.Tests;

/// <summary>
/// Phase 11 Tests (Live): AI Chat with a real LLM backend.
/// Ported from tests/tests/test_phase11_ai_chat_live.py.
///
/// These tests make real calls to the configured LLM provider and are gated on
/// <see cref="TestSettings.AiTestApiKey"/> (the AI_TEST_API_KEY env var). Each test
/// returns immediately (pass-by-skip) when the key is unset, mirroring Python's
/// module-level `pytest.skip(..., allow_module_level=True)` — xUnit 2.x has no
/// built-in Skip.If and this suite deliberately does not add the Xunit.SkippableFact
/// package for a single skip check (Task 19 binding decision). `[Trait("Category",
/// "LiveAI")]` lets callers exclude these via `--filter "Category!=LiveAI"`.
///
/// All AI calls use a 60-second timeout since real responses can be slow. Entity
/// names include a random suffix to avoid conflicts between test runs.
/// </summary>
[Collection("Sequential")]
public class Phase11_AIChatLiveTests : IAsyncLifetime
{
    private readonly BrowserFixture _fixture;
    private readonly ITestOutputHelper _output;
    private IPage? _page;

    private const int AiTimeout = 60_000;

    public Phase11_AIChatLiveTests(BrowserFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        // ponytail: skip the (unnecessary) browser context entirely when there's no API
        // key — every test method will short-circuit via SkipIfNoApiKey() before touching _page.
        if (TestSettings.AiTestApiKey == null) return;
        _page = await _fixture.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        if (_page != null) await _page.Context.DisposeAsync();
    }

    /// <summary>Logs and returns true when no live AI key is configured; caller should return immediately.</summary>
    private bool SkipIfNoApiKey()
    {
        if (TestSettings.AiTestApiKey != null) return false;
        _output.WriteLine("SKIPPED: AI_TEST_API_KEY not set — live AI tests require a real API key.");
        return true;
    }

    private static string UniqueName(string prefix = "AITest") =>
        prefix + Guid.NewGuid().ToString("N")[..6];

    /// <summary>Navigate to Custom Class ListView and wait for grid.</summary>
    private async Task<(NavigationPage Nav, ListViewPage Lv)> NavToCustomClassAsync()
    {
        var nav = new NavigationPage(_page!);
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        var lv = new ListViewPage(_page!);
        await lv.WaitForGridAsync();
        return (nav, lv);
    }

    /// <summary>Delete a row from the current grid if it exists.</summary>
    private async Task DeleteIfExistsAsync(string text)
    {
        var lv = new ListViewPage(_page!);
        if (await lv.HasRowWithTextAsync(text))
        {
            await lv.SelectRowWithTextAsync(text);
            await lv.ClickDeleteAsync();
            await lv.ConfirmDeleteAsync();
            await _page!.WaitForTimeoutAsync(500);
        }
    }

    /// <summary>Navigate to Custom Class list and delete entity by name if it exists (best-effort, matches Python's bare except).</summary>
    private async Task CleanupEntityAsync(string name)
    {
        try
        {
            await NavToCustomClassAsync();
            await DeleteIfExistsAsync(name);
        }
        catch
        {
            // ponytail: best-effort cleanup, matches Python's bare try/except.
        }
    }

    // --- TestLiveEntityCreation ---

    /// <summary>Ask the AI to create an entity, confirm the action, and verify it appears in the CustomClass list view.</summary>
    [Fact]
    [Trait("Category", "LiveAI")]
    public async Task Test_01_CreateEntityNaturalLanguage()
    {
        if (SkipIfNoApiKey()) return;

        var entityName = UniqueName("AICreate");
        var chat = new AIChatPanel(_page!);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        await chat.SendMessageAsync(
            $"Create a new entity called {entityName} with fields: " +
            "Name (string), Age (integer), Email (string). " +
            "Put it in the Testing navigation group.",
            AiTimeout);

        var response = await chat.GetLastResponseAsync();
        Assert.True(response.Length > 0, "AI returned empty response");

        Assert.True(response.ToLowerInvariant().Contains(entityName.ToLowerInvariant()),
            $"AI response does not mention entity name '{entityName}': {response[..Math.Min(200, response.Length)]}");

        // If the AI asks for confirmation, confirm
        if (response.Contains('?') || response.ToLowerInvariant().Contains("confirm"))
            await chat.SendMessageAsync("Yes, please create it.", AiTimeout);

        try
        {
            var (_, lv) = await NavToCustomClassAsync();
            await _page!.WaitForTimeoutAsync(2000);
            Assert.True(await lv.HasRowWithTextAsync(entityName),
                $"Entity '{entityName}' not found in Custom Class list after AI creation");
        }
        finally
        {
            await CleanupEntityAsync(entityName);
        }
    }

    // --- TestLiveEntityModification ---

    /// <summary>Create an entity first, then ask the AI to add a field to it.</summary>
    [Fact]
    [Trait("Category", "LiveAI")]
    public async Task Test_02_AddFieldViaChat()
    {
        if (SkipIfNoApiKey()) return;

        var entityName = UniqueName("AIModify");
        var chat = new AIChatPanel(_page!);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        // First, ask the AI to create a simple entity
        await chat.SendMessageAsync($"Create an entity called {entityName} with a Name field (string).", AiTimeout);
        var response = await chat.GetLastResponseAsync();
        Assert.True(response.Length > 0, "AI returned empty response for creation");

        if (response.Contains('?') || response.ToLowerInvariant().Contains("confirm"))
            await chat.SendMessageAsync("Yes, go ahead.", AiTimeout);

        // Now ask to add a new field
        await chat.SendMessageAsync($"Add a Salary field of type decimal to {entityName}.", AiTimeout);
        var modifyResponse = await chat.GetLastResponseAsync();
        Assert.True(modifyResponse.Length > 0, "AI returned empty response for modification");

        if (modifyResponse.Contains('?') || modifyResponse.ToLowerInvariant().Contains("confirm"))
            await chat.SendMessageAsync("Yes, add it.", AiTimeout);

        // The AI should mention the field was added or the salary field
        var finalResponse = await chat.GetLastResponseAsync();
        var lower = finalResponse.ToLowerInvariant();
        Assert.True(lower.Contains("salary") || lower.Contains("added"),
            $"AI response does not confirm field addition: {finalResponse[..Math.Min(200, finalResponse.Length)]}");

        await CleanupEntityAsync(entityName);
    }

    // --- TestLiveAmbiguityResolution ---

    /// <summary>Send a vague request and verify the AI asks for clarification rather than guessing.</summary>
    [Fact]
    [Trait("Category", "LiveAI")]
    public async Task Test_03_VagueRequest()
    {
        if (SkipIfNoApiKey()) return;

        var chat = new AIChatPanel(_page!);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        await chat.SendMessageAsync("I need to track some stuff", AiTimeout);

        var response = await chat.GetLastResponseAsync();
        Assert.True(response.Length > 0, "AI returned empty response");

        // The AI should ask a clarifying question — look for question marks
        // or typical clarification language
        var lower = response.ToLowerInvariant();
        var hasQuestion = response.Contains('?');
        string[] clarificationWords =
        [
            "what", "which", "could you", "can you", "more detail",
            "specify", "clarify", "tell me more", "what kind",
        ];
        var hasClarification = clarificationWords.Any(lower.Contains);
        Assert.True(hasQuestion || hasClarification,
            $"AI did not ask for clarification on vague request. Response: {response[..Math.Min(300, response.Length)]}");
    }

    // --- TestLiveRolePermissions ---

    /// <summary>Ask the AI about roles/permissions and verify it gives a meaningful answer.</summary>
    [Fact]
    [Trait("Category", "LiveAI")]
    public async Task Test_04_AskAboutPermissions()
    {
        if (SkipIfNoApiKey()) return;

        var chat = new AIChatPanel(_page!);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        await chat.SendMessageAsync("What roles are available in the system?", AiTimeout);

        var response = await chat.GetLastResponseAsync();
        Assert.True(response.Length > 0, "AI returned empty response");

        // The AI should mention something about roles — either listing them
        // or explaining the role system
        var lower = response.ToLowerInvariant();
        string[] roleWords = ["role", "admin", "permission", "user", "access"];
        Assert.True(roleWords.Any(lower.Contains),
            $"AI response does not contain role-related content: {response[..Math.Min(300, response.Length)]}");
    }

    // --- TestLiveMultiTurn ---

    /// <summary>
    /// Full workflow: create entity, confirm, add field, confirm.
    /// Verify the conversation has at least 6 messages (3 user + 3 assistant).
    /// </summary>
    [Fact]
    [Trait("Category", "LiveAI")]
    public async Task Test_05_CreateThenModify()
    {
        if (SkipIfNoApiKey()) return;

        var entityName = UniqueName("AIMulti");
        var chat = new AIChatPanel(_page!);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        // Turn 1: Ask to create an entity
        await chat.SendMessageAsync(
            $"Create an entity called {entityName} with a Name field (string) in the Testing group.",
            AiTimeout);
        var response1 = await chat.GetLastResponseAsync();
        Assert.True(response1.Length > 0, "Empty response on turn 1");

        // Turn 2: Confirm creation
        await chat.SendMessageAsync("Yes, create it please.", AiTimeout);
        var response2 = await chat.GetLastResponseAsync();
        Assert.True(response2.Length > 0, "Empty response on turn 2");

        // Turn 3: Ask to modify
        await chat.SendMessageAsync($"Now add an IsActive field (boolean) to {entityName}.", AiTimeout);
        var response3 = await chat.GetLastResponseAsync();
        Assert.True(response3.Length > 0, "Empty response on turn 3");

        // Confirm modification if needed
        if (response3.Contains('?') || response3.ToLowerInvariant().Contains("confirm"))
            await chat.SendMessageAsync("Yes, add it.", AiTimeout);

        // Verify conversation length — at least 6 messages (3 user + 3 assistant)
        var msgCount = await chat.GetMessageCountAsync();
        Assert.True(msgCount >= 6, $"Expected at least 6 messages in multi-turn conversation, got {msgCount}");

        await CleanupEntityAsync(entityName);
    }
}
