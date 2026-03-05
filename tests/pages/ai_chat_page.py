from playwright.sync_api import Page
from .base_page import BasePage
from .navigation_page import NavigationPage


class AIChatPanel(BasePage):
    """Page object for interacting with the DxAIChat component.

    The chat panel is rendered inside a .copilot-chat-container div containing
    a DevExpress DxAIChat component with CssClass="copilot-chat".

    DxAIChat renders as a custom element tree. Key selectors:
    - Container: .copilot-chat-container
    - Chat component: .copilot-chat (DxAIChat root)
    - Message input: textarea inside the chat footer area
    - Send button: button in the chat input area
    - Messages: rendered inside message list areas with role-based classes
    - Prompt suggestions: rendered as clickable buttons/cards
    - Empty state: .copilot-empty-area

    NOTE: DevExpress DxAIChat internal DOM structure may vary across versions.
    The selectors below are based on DevExpress AIIntegration.Blazor.Chat and
    may need adjustment if the DxAIChat version changes.
    """

    # -- Container selectors --
    CHAT_CONTAINER = ".copilot-chat-container"
    CHAT_ROOT = ".copilot-chat"
    EMPTY_AREA = ".copilot-empty-area"

    # -- DxAIChat internal selectors --
    # DxAIChat renders messages in a scrollable area. Each message has a role
    # attribute or CSS class indicating user vs assistant.
    MESSAGE_INPUT = ".copilot-chat textarea"
    SEND_BUTTON = ".copilot-chat .dxai-chat-button-send, .copilot-chat button[aria-label='Send']"

    # Message selectors — DxAIChat uses .dxai-chat-message with role attributes
    ALL_MESSAGES = ".copilot-chat .dxai-chat-message"
    ASSISTANT_MESSAGES = ".copilot-chat .dxai-chat-message-assistant"
    USER_MESSAGES = ".copilot-chat .dxai-chat-message-user"

    # Message content is rendered inside the message bubble
    MESSAGE_CONTENT = ".dxai-chat-message-content"

    # Loading/typing indicator shown while AI is responding
    LOADING_INDICATOR = ".copilot-chat .dxai-chat-message-typing, .copilot-chat .dxai-chat-typing-indicator"

    # Prompt suggestion buttons
    SUGGESTION_BUTTON = ".copilot-chat .dxai-chat-prompt-suggestion"

    def __init__(self, page: Page):
        super().__init__(page)
        self._nav = NavigationPage(page)

    def is_visible(self) -> bool:
        """Check if the chat panel is visible."""
        return self.page.locator(self.CHAT_CONTAINER).is_visible()

    def wait_for_panel(self, timeout: int = 10000):
        """Wait for the chat panel to appear in the DOM and become visible."""
        self.page.locator(self.CHAT_CONTAINER).wait_for(state="visible", timeout=timeout)
        self.page.wait_for_timeout(500)

    def navigate_to_chat(self):
        """Navigate to the AI Chat view via the navigation pane.

        Tries common navigation group/item names. The exact name depends on
        how the navigation item is registered in Module.cs.
        """
        # Try direct URL navigation first (most reliable for XAF views)
        try:
            self._nav.navigate_to_item("AI Chat")
        except ValueError:
            try:
                self._nav.navigate_to_item("Copilot")
            except ValueError:
                # Fallback: navigate to the view URL directly
                base_url = self.page.url.split("//")[0] + "//" + self.page.url.split("//")[1].split("/")[0]
                self.page.goto(f"{base_url}/AIChatView")
                self.wait_for_loading()
        self.page.wait_for_timeout(500)

    def send_message(self, text: str, timeout: int = 30000):
        """Type a message in the chat input and send it, then wait for the response.

        Args:
            text: The message text to send.
            timeout: Maximum time in ms to wait for the AI response.
        """
        input_el = self.page.locator(self.MESSAGE_INPUT).first
        input_el.wait_for(state="visible", timeout=5000)
        input_el.click()
        input_el.fill(text)
        self.page.wait_for_timeout(200)

        # Try clicking the send button; fall back to pressing Enter
        send_btn = self.page.locator(self.SEND_BUTTON)
        if send_btn.count() > 0 and send_btn.first.is_visible():
            send_btn.first.click()
        else:
            input_el.press("Enter")

        self.page.wait_for_timeout(500)
        self.wait_for_response(timeout=timeout)

    def wait_for_response(self, timeout: int = 30000):
        """Wait for the AI to finish responding.

        Waits for:
        1. At least one assistant message to appear.
        2. The loading/typing indicator to disappear.
        """
        # Wait for an assistant message to appear
        self.page.locator(self.ASSISTANT_MESSAGES).first.wait_for(
            state="visible", timeout=timeout
        )

        # Wait for typing indicator to disappear (streaming complete)
        try:
            loading = self.page.locator(self.LOADING_INDICATOR)
            if loading.count() > 0 and loading.first.is_visible():
                loading.first.wait_for(state="hidden", timeout=timeout)
        except Exception:
            pass  # Indicator may have already disappeared

        self.page.wait_for_timeout(500)

    def get_last_response(self) -> str:
        """Get the text content of the last assistant message."""
        messages = self.page.locator(self.ASSISTANT_MESSAGES)
        count = messages.count()
        if count == 0:
            return ""
        last = messages.nth(count - 1)
        content = last.locator(self.MESSAGE_CONTENT)
        if content.count() > 0:
            return content.first.inner_text()
        return last.inner_text()

    def get_last_response_html(self) -> str:
        """Get the inner HTML of the last assistant message (for markdown verification)."""
        messages = self.page.locator(self.ASSISTANT_MESSAGES)
        count = messages.count()
        if count == 0:
            return ""
        last = messages.nth(count - 1)
        content = last.locator(self.MESSAGE_CONTENT)
        if content.count() > 0:
            return content.first.inner_html()
        return last.inner_html()

    def get_all_responses(self) -> list[str]:
        """Get text content of all assistant messages."""
        messages = self.page.locator(self.ASSISTANT_MESSAGES)
        results = []
        for i in range(messages.count()):
            msg = messages.nth(i)
            content = msg.locator(self.MESSAGE_CONTENT)
            if content.count() > 0:
                results.append(content.first.inner_text())
            else:
                results.append(msg.inner_text())
        return results

    def get_message_count(self) -> int:
        """Get the total number of messages (user + assistant) in the chat."""
        return self.page.locator(self.ALL_MESSAGES).count()

    def click_suggestion(self, text: str):
        """Click a prompt suggestion button by its visible text.

        Args:
            text: Text (or partial text) of the suggestion to click.
        """
        suggestions = self.page.locator(self.SUGGESTION_BUTTON)
        for i in range(suggestions.count()):
            suggestion = suggestions.nth(i)
            if text.lower() in (suggestion.inner_text() or "").lower():
                # Use JS click for Blazor reliability
                self.page.evaluate("el => el.click()", suggestion.element_handle())
                self.page.wait_for_timeout(500)
                return
        raise ValueError(f"No prompt suggestion found containing text: {text}")

    def has_table_in_last_response(self) -> bool:
        """Check if the last assistant message contains a rendered HTML table."""
        messages = self.page.locator(self.ASSISTANT_MESSAGES)
        count = messages.count()
        if count == 0:
            return False
        last = messages.nth(count - 1)
        return last.locator("table").count() > 0

    def response_contains(self, text: str) -> bool:
        """Check if the last assistant response contains the given text (case-insensitive)."""
        response = self.get_last_response()
        return text.lower() in response.lower()

    def is_empty_state_visible(self) -> bool:
        """Check if the empty state area (shown before any messages) is visible."""
        return self.page.locator(self.EMPTY_AREA).is_visible()

    def get_empty_state_text(self) -> str:
        """Get the text content of the empty state area."""
        empty = self.page.locator(self.EMPTY_AREA)
        if empty.count() > 0:
            return empty.first.inner_text()
        return ""
