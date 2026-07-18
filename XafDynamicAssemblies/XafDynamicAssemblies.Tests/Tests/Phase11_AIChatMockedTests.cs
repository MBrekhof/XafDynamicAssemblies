using XafDynamicAssemblies.Tests.Fixtures;
using XafDynamicAssemblies.Tests.Pages;

namespace XafDynamicAssemblies.Tests.Tests;

/// <summary>
/// Phase 11 Tests: AI Chat UI with mocked LLM server.
/// Ported from tests/tests/test_phase11_ai_chat_mocked.py.
///
/// Tests the DxAIChat Copilot panel against the deterministic mock LLM server
/// (<see cref="MockLlmFixture"/>). Covers chat visibility, message send/receive,
/// entity CRUD proposals, validation, pending changes, role permissions, and
/// multi-turn conversation continuity.
///
/// The running XAF app must be started with the AI_MOCK_LLM_BASE_URL env var
/// pointing at this fixture's mock server port (see AIOptions.MockLlmBaseUrlEnvVar /
/// TornadoApiProvider) so AIChatService routes LLM calls to it instead of a real
/// provider.
/// </summary>
[Collection("Sequential")]
public class Phase11_AIChatMockedTests : IAsyncLifetime, IClassFixture<MockLlmFixture>
{
    private readonly BrowserFixture _fixture;
    private IPage _page = null!;

    private static readonly HttpClient MockHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    // Class-fixture-scoped mock server (started once, shared across all test methods in this
    // class) — mirrors Python's session-scoped mock_llm_server fixture. Not referenced directly;
    // xunit constructs/disposes it around the test class lifetime.
    public Phase11_AIChatMockedTests(BrowserFixture fixture, MockLlmFixture mockLlm) => _fixture = fixture;

    public async Task InitializeAsync() => _page = await _fixture.NewPageAsync();

    public async Task DisposeAsync() => await _page.Context.DisposeAsync();

    /// <summary>Reset the mock LLM server's conversation state between tests (best-effort, matches Python).</summary>
    private static async Task ResetMockStateAsync()
    {
        try
        {
            await MockHttp.PostAsync($"http://localhost:{TestSettings.MockLlmPort}/reset", null);
        }
        catch
        {
            // ponytail: matches Python's bare try/except — reset is best-effort.
        }
    }

    /// <summary>Navigate to Custom Class ListView and wait for grid.</summary>
    private async Task<(NavigationPage Nav, ListViewPage Lv)> NavToCustomClassAsync()
    {
        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        return (nav, lv);
    }

    /// <summary>Delete a row from the current grid if it exists.</summary>
    private async Task DeleteIfExistsAsync(string text)
    {
        var lv = new ListViewPage(_page);
        if (await lv.HasRowWithTextAsync(text))
        {
            await lv.SelectRowWithTextAsync(text);
            await lv.ClickDeleteAsync();
            await lv.ConfirmDeleteAsync();
            await _page.WaitForTimeoutAsync(500);
        }
    }

    // --- TestAIChatUIBasics: chat panel visibility, message rendering, prompt suggestions ---

    /// <summary>Verify the AI Chat view loads and the chat panel is visible.</summary>
    [Fact]
    public async Task Test_01_ChatPanelVisible()
    {
        var chat = new AIChatPanel(_page);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);
        Assert.True(await chat.IsVisibleAsync(), "Chat panel should be visible after navigation");
    }

    /// <summary>Send a generic message and verify the mock returns a response.</summary>
    [Fact]
    public async Task Test_02_SendMessageGetsResponse()
    {
        await ResetMockStateAsync();
        var chat = new AIChatPanel(_page);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        await chat.SendMessageAsync("Hello, what can you do?", 30_000);
        var response = await chat.GetLastResponseAsync();
        Assert.True(response.Length > 0, "Should receive a non-empty response from the mock LLM");
        // The default response from ScriptMatcher mentions creating/modifying/deleting entities
        Assert.True(await chat.ResponseContainsAsync("entity") || await chat.ResponseContainsAsync("help"),
            $"Expected default response about entities, got: {response}");
    }

    /// <summary>Click a prompt suggestion and verify a response is generated.</summary>
    [Fact]
    public async Task Test_03_PromptSuggestionWorks()
    {
        await ResetMockStateAsync();
        var chat = new AIChatPanel(_page);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        // Check if empty state or suggestions are visible
        if (await chat.IsEmptyStateVisibleAsync())
        {
            var emptyText = await chat.GetEmptyStateTextAsync();
            Assert.True(emptyText.Length > 0, "Empty state should show introductory text");
        }

        // If suggestion buttons exist, click one
        var suggestions = _page.Locator(AIChatPanel.SuggestionButton);
        if (await suggestions.CountAsync() > 0)
        {
            var firstText = await suggestions.First.InnerTextAsync();
            await chat.ClickSuggestionAsync(firstText);
            await chat.WaitForResponseAsync(30_000);
            var response = await chat.GetLastResponseAsync();
            Assert.True(response.Length > 0, "Clicking a suggestion should produce a response");
        }
        else
        {
            // No suggestions visible; send a manual message instead
            await chat.SendMessageAsync("list entities", 30_000);
            var response = await chat.GetLastResponseAsync();
            Assert.True(response.Length > 0, "Should get a response for 'list entities'");
        }
    }

    /// <summary>Verify that markdown content renders properly in the chat.</summary>
    [Fact]
    public async Task Test_04_MarkdownTableRenders()
    {
        await ResetMockStateAsync();
        var chat = new AIChatPanel(_page);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        // The "create" response includes markdown bold and list items
        await chat.SendMessageAsync("create a TestMarkdownEntity", 30_000);
        var response = await chat.GetLastResponseAsync();
        Assert.True(response.Contains("TestMarkdownEntity"),
            $"Response should mention the entity name, got: {response}");

        // Check that markdown was rendered (bold text becomes HTML tags)
        var html = await chat.GetLastResponseHtmlAsync();
        // DxAIChat should render **text** as <strong> or <b>
        var hasBold = html.ToLowerInvariant().Contains("<strong>") || html.ToLowerInvariant().Contains("<b>");
        var hasList = html.ToLowerInvariant().Contains("<li>") || response.Contains("- ");
        Assert.True(hasBold || hasList,
            $"Markdown should be rendered as HTML. Got HTML: {html[..Math.Min(500, html.Length)]}");
    }

    // --- TestEntityCreationFlow: create entity via natural language conversation ---

    /// <summary>Send a 'create entity' message and verify the proposal response.</summary>
    [Fact]
    public async Task Test_05_ProposeEntity()
    {
        await ResetMockStateAsync();
        var chat = new AIChatPanel(_page);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        await chat.SendMessageAsync("create a ChatTestEntity", 30_000);
        var response = await chat.GetLastResponseAsync();
        Assert.True(response.Contains("ChatTestEntity"),
            $"Proposal should mention the entity name, got: {response}");
        Assert.True(response.ToLowerInvariant().Contains("field"),
            $"Proposal should mention fields, got: {response}");
        Assert.True(response.ToLowerInvariant().Contains("look good") || response.ToLowerInvariant().Contains("confirm"),
            $"Proposal should ask for confirmation, got: {response}");
    }

    /// <summary>Confirm the entity creation proposal.</summary>
    [Fact]
    public async Task Test_06_ConfirmCreation()
    {
        // Continue from previous test's conversation state
        // The mock server has _pending_entity set from the create message
        var chat = new AIChatPanel(_page);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        // First propose the entity (fresh context per test due to function-scoped page)
        await ResetMockStateAsync();
        await chat.SendMessageAsync("create a ChatTestEntity", 30_000);
        await _page.WaitForTimeoutAsync(1000);

        // Now confirm
        await chat.SendMessageAsync("yes", 30_000);
        var response = await chat.GetLastResponseAsync();
        // After tool execution, the follow-up says "Created the entity"
        var lower = response.ToLowerInvariant();
        Assert.True(lower.Contains("creat") || lower.Contains("done") || lower.Contains("deploy"),
            $"Confirmation response should indicate creation, got: {response}");
    }

    /// <summary>
    /// After AI-assisted creation, verify the entity exists in Custom Class list.
    ///
    /// NOTE: This test depends on the AI chat actually executing the create_entity
    /// tool call against the XAF backend. If the mock server only returns tool_use
    /// responses without backend integration, this test verifies the tool call
    /// was generated correctly instead.
    /// </summary>
    [Fact]
    public async Task Test_07_EntityExistsInMetadata()
    {
        await ResetMockStateAsync();
        var chat = new AIChatPanel(_page);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        // Propose and confirm creation
        await chat.SendMessageAsync("create a ChatTestVerify", 30_000);
        await _page.WaitForTimeoutAsync(500);
        await chat.SendMessageAsync("yes", 30_000);

        // The mock server returns a tool_use for create_entity
        // Check if the response indicates the tool was called
        var responses = await chat.GetAllResponsesAsync();
        var last = responses.Count > 0 ? responses[^1] : "";
        // The tool result follow-up says "Created the entity"
        var lower = last.ToLowerInvariant();
        Assert.True(lower.Contains("creat") || lower.Contains("entity"),
            $"Expected creation confirmation, got: {last}");
    }

    // --- TestEntityModificationFlow: adding fields to entities via chat ---

    /// <summary>Send an 'add field' message and verify the proposal.</summary>
    [Fact]
    public async Task Test_08_AddFieldProposal()
    {
        await ResetMockStateAsync();
        var chat = new AIChatPanel(_page);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        await chat.SendMessageAsync("add a field 'Email' to 'Customer'", 30_000);
        var response = await chat.GetLastResponseAsync();
        var lower = response.ToLowerInvariant();
        Assert.True(lower.Contains("email"), $"Response should mention the field name, got: {response}");
        Assert.True(lower.Contains("customer"), $"Response should mention the entity name, got: {response}");
        Assert.True(lower.Contains("look good") || lower.Contains("confirm"),
            $"Response should ask for confirmation, got: {response}");
    }

    // --- TestEntityDeletionFlow: entity deletion proposals via chat ---

    /// <summary>Send a 'delete entity' message and verify the warning.</summary>
    [Fact]
    public async Task Test_09_DeleteProposal()
    {
        await ResetMockStateAsync();
        var chat = new AIChatPanel(_page);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        await chat.SendMessageAsync("delete entity 'ObsoleteEntity'", 30_000);
        var response = await chat.GetLastResponseAsync();
        Assert.True(response.Contains("ObsoleteEntity"),
            $"Response should mention the entity to delete, got: {response}");
        var lower = response.ToLowerInvariant();
        Assert.True(lower.Contains("confirm") || lower.Contains("sure"),
            $"Response should ask for confirmation before deletion, got: {response}");
        Assert.True(lower.Contains("data") || lower.Contains("remove"),
            $"Response should warn about data loss, got: {response}");
    }

    // --- TestValidation: schema validation via chat ---

    /// <summary>Send a 'validate schema' message and check for tool invocation response.</summary>
    [Fact]
    public async Task Test_10_ValidateSchema()
    {
        await ResetMockStateAsync();
        var chat = new AIChatPanel(_page);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        await chat.SendMessageAsync("validate the schema", 30_000);
        var response = await chat.GetLastResponseAsync();
        // The mock returns a tool_use for validate_schema, then follow-up text
        var lower = response.ToLowerInvariant();
        Assert.True(lower.Contains("validat") || lower.Contains("schema") || lower.Contains("compil"),
            $"Expected validation-related response, got: {response}");
    }

    // --- TestPendingChanges: pending changes display via chat ---

    /// <summary>Send a 'show pending changes' message and verify response.</summary>
    [Fact]
    public async Task Test_11_ShowPendingChanges()
    {
        await ResetMockStateAsync();
        var chat = new AIChatPanel(_page);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        await chat.SendMessageAsync("show pending changes", 30_000);
        var response = await chat.GetLastResponseAsync();
        var lower = response.ToLowerInvariant();
        Assert.True(lower.Contains("pending") || lower.Contains("change"),
            $"Expected pending changes response, got: {response}");
    }

    // --- TestRolePermissions: role listing and permission management via chat ---

    /// <summary>Send 'list roles' and verify the tool is invoked.</summary>
    [Fact]
    public async Task Test_12_ListRoles()
    {
        await ResetMockStateAsync();
        var chat = new AIChatPanel(_page);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        await chat.SendMessageAsync("list roles", 30_000);
        var response = await chat.GetLastResponseAsync();
        Assert.True(response.ToLowerInvariant().Contains("role"),
            $"Expected roles-related response, got: {response}");
    }

    /// <summary>Send a permissions request and verify the response.</summary>
    [Fact]
    public async Task Test_13_SetPermissionsProposal()
    {
        await ResetMockStateAsync();
        var chat = new AIChatPanel(_page);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        await chat.SendMessageAsync("set permission for Customer entity", 30_000);
        var response = await chat.GetLastResponseAsync();
        var lower = response.ToLowerInvariant();
        Assert.True(lower.Contains("permission") || lower.Contains("access"),
            $"Expected permissions-related response, got: {response}");
    }

    // --- TestConversationContinuity: multi-turn conversation flows ---

    /// <summary>Verify that a multi-turn conversation accumulates messages correctly.</summary>
    [Fact]
    public async Task Test_14_MultiTurn()
    {
        await ResetMockStateAsync();
        var chat = new AIChatPanel(_page);
        await chat.NavigateToChatAsync();
        await chat.WaitForPanelAsync(15_000);

        // Turn 1: propose an entity
        await chat.SendMessageAsync("create a MultiTurnTest", 30_000);
        var firstResponse = await chat.GetLastResponseAsync();
        Assert.True(firstResponse.Contains("MultiTurnTest"),
            $"First response should mention entity name, got: {firstResponse}");

        // Check message count after first exchange (1 user + 1 assistant = 2)
        var countAfterFirst = await chat.GetMessageCountAsync();
        Assert.True(countAfterFirst >= 2,
            $"Expected at least 2 messages after first exchange, got {countAfterFirst}");

        // Turn 2: confirm creation
        await chat.SendMessageAsync("yes", 30_000);
        var secondResponse = await chat.GetLastResponseAsync();
        Assert.True(secondResponse.Length > 0, "Second turn should produce a response");

        // Message count should increase
        var countAfterSecond = await chat.GetMessageCountAsync();
        Assert.True(countAfterSecond > countAfterFirst,
            $"Message count should increase: {countAfterSecond} > {countAfterFirst}");

        // All responses should be retrievable
        var allResponses = await chat.GetAllResponsesAsync();
        Assert.True(allResponses.Count >= 2, $"Should have at least 2 assistant responses, got {allResponses.Count}");
    }

    // --- TestCleanup ---

    /// <summary>Remove any entities created by AI chat tests.</summary>
    [Fact]
    public async Task Test_99_Cleanup()
    {
        await ResetMockStateAsync();
        await NavToCustomClassAsync();
        foreach (var name in new[]
                 {
                     "ChatTestEntity",
                     "ChatTestVerify",
                     "TestMarkdownEntity",
                     "MultiTurnTest",
                     "NewEntity",
                 })
        {
            await DeleteIfExistsAsync(name);
            await _page.WaitForTimeoutAsync(300);
        }
    }
}
