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
    private const string ChatContainer = ".copilot-chat-container";
    private const string MessageInput = ".copilot-chat textarea";
    private const string SendButton = ".copilot-chat .dxai-chat-button-send, .copilot-chat button[aria-label='Send']";
    private const string AllMessages = ".copilot-chat .dxai-chat-message";
    private const string AssistantMessages = ".copilot-chat .dxai-chat-message-assistant";
    private const string MessageContent = ".dxai-chat-message-content";
    private const string LoadingIndicator = ".copilot-chat .dxai-chat-message-typing, .copilot-chat .dxai-chat-typing-indicator";

    // Public: referenced directly by test_phase11_ai_chat_mocked.py (AIChatPanel.SUGGESTION_BUTTON)
    // for a locator built outside the page object — kept public here for the same reason.
    public const string SuggestionButton = ".copilot-chat .dxai-chat-prompt-suggestion";

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
    /// Navigate to the AI Chat view via the navigation pane. Tries "AI Chat" then "Copilot"
    /// nav items, falling back to direct URL navigation if neither is found.
    /// </summary>
    public async Task NavigateToChatAsync()
    {
        try
        {
            await _nav.NavigateToItemAsync("AI Chat");
        }
        catch (InvalidOperationException)
        {
            try
            {
                await _nav.NavigateToItemAsync("Copilot");
            }
            catch (InvalidOperationException)
            {
                await Page.GotoAsync($"{TestSettings.BaseUrl}/AIChatView");
                await WaitForLoadingAsync();
            }
        }
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

        // Try clicking the send button; fall back to pressing Enter
        var sendBtn = Page.Locator(SendButton);
        if (await sendBtn.CountAsync() > 0 && await sendBtn.First.IsVisibleAsync())
            await sendBtn.First.ClickAsync();
        else
            await input.PressAsync("Enter");

        await Page.WaitForTimeoutAsync(500);
        await WaitForResponseAsync(timeout);
    }

    /// <summary>
    /// Wait for the AI to finish responding: an assistant message appears, then the
    /// loading/typing indicator (if any) disappears.
    /// </summary>
    public async Task WaitForResponseAsync(int timeout = 30_000)
    {
        await Page.Locator(AssistantMessages).First
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = timeout });

        try
        {
            var loading = Page.Locator(LoadingIndicator);
            if (await loading.CountAsync() > 0 && await loading.First.IsVisibleAsync())
            {
                await loading.First.WaitForAsync(new()
                {
                    State = WaitForSelectorState.Hidden,
                    Timeout = timeout
                });
            }
        }
        catch
        {
            // Indicator may have already disappeared
        }

        await Page.WaitForTimeoutAsync(500);
    }

    /// <summary>Get the text content of the last assistant message.</summary>
    public async Task<string> GetLastResponseAsync()
    {
        var messages = Page.Locator(AssistantMessages);
        var count = await messages.CountAsync();
        if (count == 0) return "";
        var last = messages.Nth(count - 1);
        var content = last.Locator(MessageContent);
        if (await content.CountAsync() > 0)
            return await content.First.InnerTextAsync();
        return await last.InnerTextAsync();
    }

    /// <summary>Get the inner HTML of the last assistant message (for markdown verification).</summary>
    public async Task<string> GetLastResponseHtmlAsync()
    {
        var messages = Page.Locator(AssistantMessages);
        var count = await messages.CountAsync();
        if (count == 0) return "";
        var last = messages.Nth(count - 1);
        var content = last.Locator(MessageContent);
        if (await content.CountAsync() > 0)
            return await content.First.InnerHTMLAsync();
        return await last.InnerHTMLAsync();
    }

    /// <summary>Get text content of all assistant messages.</summary>
    public async Task<List<string>> GetAllResponsesAsync()
    {
        var messages = Page.Locator(AssistantMessages);
        var count = await messages.CountAsync();
        var result = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var msg = messages.Nth(i);
            var content = msg.Locator(MessageContent);
            if (await content.CountAsync() > 0)
                result.Add(await content.First.InnerTextAsync());
            else
                result.Add(await msg.InnerTextAsync());
        }
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
