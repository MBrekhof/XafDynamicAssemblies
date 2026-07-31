namespace XafDynamicAssemblies.Tests.Pages;

/// <summary>
/// Page object for interacting with the DevExpress DxAIChat component.
/// Ported from tests/pages/ai_chat_page.py.
///
/// The chat panel is rendered inside a .copilot-chat-container div containing
/// a DxAIChat component with CssClass="copilot-chat".
///
/// NOTE: DevExpress DxAIChat internal DOM structure may vary across versions.
/// The selectors below are based on DevExpress AIIntegration.Blazor.Chat and
/// may need adjustment if the DxAIChat version changes.
/// </summary>
public class AIChatPanel : BasePage
{
    // Selectors below were corrected against the live app's DevExpress DxAIChat 25.2.3 DOM
    // (Task 18) — the Python-parity guesses (dxai-chat-* classes) never matched any element;
    // see task-18-report.md for the verification trail.
    private const string ChatContainer = ".copilot-chat-container";
    private const string MessageInput = ".copilot-chat textarea";
    // ponytail: SendButton/MessageContent/LoadingIndicator selectors removed — task-18-report.md
    // confirmed all three never matched anything in the live DxAIChat DOM (100% dead, not just the
    // dxai-chat-* halves). Code always took the fallback path anyway: Enter-key send
    // (SendMessageAsync/ClickSuggestionAsync), whole-message-div InnerText/InnerHTML
    // (GetLastResponseAsync/GetLastResponseHtmlAsync/GetAllResponsesAsync), no wait-for-hidden
    // (WaitForResponseAsync). Kept only that working behavior.
    private const string AllMessages = ".copilot-chat .dxbl-chatui-message";
    private const string AssistantMessages = ".copilot-chat .dxbl-chatui-message-assistant";

    // Public: referenced directly by test_phase11_ai_chat_mocked.py (AIChatPanel.SUGGESTION_BUTTON)
    // for a locator built outside the page object — kept public here for the same reason.
    public const string SuggestionButton = ".copilot-chat .dxbl-chatui-prompt-suggestion";

    private const string EmptyArea = ".copilot-empty-area";

    private readonly NavigationPage _nav;

    public AIChatPanel(IPage page) : base(page)
    {
        _nav = new NavigationPage(page);
    }

    /// <summary>Check if the chat panel is visible.</summary>
    public async Task<bool> IsVisibleAsync() =>
        await Page.Locator(ChatContainer).IsVisibleAsync();

    /// <summary>Wait for the chat panel to appear in the DOM and become visible.</summary>
    public async Task WaitForPanelAsync(int timeout = 10_000)
    {
        await Page.Locator(ChatContainer)
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = timeout });
        await Page.WaitForTimeoutAsync(500);
    }

    /// <summary>
    /// Navigate to the AI Chat view via the navigation pane. The nav item is the
    /// non-persistent <c>AIChat</c> DomainComponent, listed under "Schema Management"
    /// with caption "AIChat" (verified against the live app — the Python-parity guesses
    /// "AI Chat" / "Copilot" / direct "/AIChatView" URL never matched anything real:
    /// "AI Chat" has a space the actual caption doesn't, and "/AIChatView" isn't a valid
    /// XAF view id, so it 404'd via ShowAIChatController's shortcut handling).
    /// </summary>
    public async Task NavigateToChatAsync()
    {
        await _nav.NavigateToAsync("Schema Management", "AIChat");
        await Page.WaitForTimeoutAsync(500);
    }

    /// <summary>Type a message in the chat input and send it, then wait for the response.</summary>
    public async Task SendMessageAsync(string text, int timeout = 30_000)
    {
        var input = Page.Locator(MessageInput).First;
        await input.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
        await input.ClickAsync();
        await input.FillAsync(text);
        await Page.WaitForTimeoutAsync(200);
        await input.PressAsync("Enter");

        await Page.WaitForTimeoutAsync(500);
        await WaitForResponseAsync(timeout);
    }

    /// <summary>Wait for the AI to finish responding: the last assistant message has text.</summary>
    public async Task WaitForResponseAsync(int timeout = 30_000)
    {
        // An assistant bubble can be visible before any text exists: DxAIChat renders a
        // tool_use turn as an empty assistant message while the server-side tool executes
        // (validate_schema's Roslyn compile takes seconds), and the typing placeholder
        // behaves the same on cold-start latency. Waiting for bubble visibility alone made
        // GetLastResponseAsync race the content (Test_02/Test_10 read "").
        await Page.WaitForFunctionAsync(
            "sel => { const m = document.querySelectorAll(sel); " +
            "return m.length > 0 && m[m.length - 1].innerText.trim().length > 0; }",
            AssistantMessages,
            new() { Timeout = timeout });

        await Page.WaitForTimeoutAsync(500);
    }

    /// <summary>Get the text content of the last assistant message.</summary>
    public async Task<string> GetLastResponseAsync()
    {
        var messages = Page.Locator(AssistantMessages);
        var count = await messages.CountAsync();
        if (count == 0) return "";
        return await messages.Nth(count - 1).InnerTextAsync();
    }

    /// <summary>Get the inner HTML of the last assistant message (for markdown verification).</summary>
    public async Task<string> GetLastResponseHtmlAsync()
    {
        var messages = Page.Locator(AssistantMessages);
        var count = await messages.CountAsync();
        if (count == 0) return "";
        return await messages.Nth(count - 1).InnerHTMLAsync();
    }

    /// <summary>Get text content of all assistant messages.</summary>
    public async Task<List<string>> GetAllResponsesAsync()
    {
        var messages = Page.Locator(AssistantMessages);
        var count = await messages.CountAsync();
        var result = new List<string>();
        for (var i = 0; i < count; i++)
            result.Add(await messages.Nth(i).InnerTextAsync());
        return result;
    }

    /// <summary>Get the total number of messages (user + assistant) in the chat.</summary>
    public async Task<int> GetMessageCountAsync() =>
        await Page.Locator(AllMessages).CountAsync();

    /// <summary>Click a prompt suggestion button by its visible text (or partial text).</summary>
    public async Task ClickSuggestionAsync(string text)
    {
        var suggestions = Page.Locator(SuggestionButton);
        var count = await suggestions.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var suggestion = suggestions.Nth(i);
            var suggestionText = await suggestion.InnerTextAsync();
            if (suggestionText.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                // Use JS click for Blazor reliability
                var handle = await suggestion.ElementHandleAsync();
                await Page.EvaluateAsync("el => el.click()", handle);
                await Page.WaitForTimeoutAsync(500);

                // Clicking a suggestion only populates the message input (verified against
                // the live DxAIChat DOM) — it does not auto-submit. Submit explicitly, same
                // mechanism as SendMessageAsync.
                await Page.Locator(MessageInput).First.PressAsync("Enter");
                return;
            }
        }
        throw new InvalidOperationException($"No prompt suggestion found containing text: {text}");
    }

    /// <summary>Check if the last assistant message contains a rendered HTML table.</summary>
    public async Task<bool> HasTableInLastResponseAsync()
    {
        var messages = Page.Locator(AssistantMessages);
        var count = await messages.CountAsync();
        if (count == 0) return false;
        var last = messages.Nth(count - 1);
        return await last.Locator("table").CountAsync() > 0;
    }

    /// <summary>Check if the last assistant response contains the given text (case-insensitive).</summary>
    public async Task<bool> ResponseContainsAsync(string text)
    {
        var response = await GetLastResponseAsync();
        return response.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Check if the empty state area (shown before any messages) is visible.</summary>
    public async Task<bool> IsEmptyStateVisibleAsync() =>
        await Page.Locator(EmptyArea).IsVisibleAsync();

    /// <summary>Get the text content of the empty state area.</summary>
    public async Task<string> GetEmptyStateTextAsync()
    {
        var empty = Page.Locator(EmptyArea);
        if (await empty.CountAsync() > 0)
            return await empty.First.InnerTextAsync();
        return "";
    }
}
