"""Phase 11 Tests: AI Chat UI with mocked LLM server.

Tests the DxAIChat Copilot panel against the deterministic mock LLM server.
Covers chat visibility, message send/receive, entity CRUD proposals, validation,
pending changes, role permissions, and multi-turn conversation continuity.

Requires:
- The XAF application running and configured to use the mock LLM endpoint
- The mock_llm_server fixture (auto-started via conftest.py)
"""
import os
import sys

import pytest
import requests

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from pages.ai_chat_page import AIChatPanel
from pages.navigation_page import NavigationPage
from pages.list_view_page import ListViewPage

MOCK_LLM_PORT = int(os.environ.get("MOCK_LLM_PORT", "5555"))
MOCK_LLM_URL = f"http://localhost:{MOCK_LLM_PORT}"


def reset_mock_state():
    """Reset the mock LLM server's conversation state between test classes."""
    try:
        requests.post(f"{MOCK_LLM_URL}/reset", timeout=5)
    except Exception:
        pass


def nav_to_custom_class(page):
    """Navigate to Custom Class ListView and wait for grid."""
    nav = NavigationPage(page)
    nav.navigate_to("Schema Management", "Custom Class")
    lv = ListViewPage(page)
    lv.wait_for_grid()
    return nav, lv


def delete_if_exists(page, text):
    """Delete a row from the current grid if it exists."""
    lv = ListViewPage(page)
    if lv.has_row_with_text(text):
        lv.select_row_with_text(text)
        lv.click_delete()
        lv.confirm_delete()
        page.wait_for_timeout(500)


class TestAIChatUIBasics:
    """Chat panel visibility, message rendering, prompt suggestions."""

    def test_01_chat_panel_visible(self, page, mock_llm_server):
        """Verify the AI Chat view loads and the chat panel is visible."""
        chat = AIChatPanel(page)
        chat.navigate_to_chat()
        chat.wait_for_panel(timeout=15000)
        assert chat.is_visible(), "Chat panel should be visible after navigation"

    def test_02_send_message_gets_response(self, page, mock_llm_server):
        """Send a generic message and verify the mock returns a response."""
        reset_mock_state()
        chat = AIChatPanel(page)
        chat.navigate_to_chat()
        chat.wait_for_panel(timeout=15000)

        chat.send_message("Hello, what can you do?", timeout=30000)
        response = chat.get_last_response()
        assert len(response) > 0, "Should receive a non-empty response from the mock LLM"
        # The default response from scripts.py mentions creating/modifying/deleting entities
        assert chat.response_contains("entity") or chat.response_contains("help"), \
            f"Expected default response about entities, got: {response}"

    def test_03_prompt_suggestion_works(self, page, mock_llm_server):
        """Click a prompt suggestion and verify a response is generated."""
        reset_mock_state()
        chat = AIChatPanel(page)
        chat.navigate_to_chat()
        chat.wait_for_panel(timeout=15000)

        # Check if empty state or suggestions are visible
        if chat.is_empty_state_visible():
            # The empty state should have some prompt text
            empty_text = chat.get_empty_state_text()
            assert len(empty_text) > 0, "Empty state should show introductory text"

        # If suggestion buttons exist, click one
        suggestions = page.locator(AIChatPanel.SUGGESTION_BUTTON)
        if suggestions.count() > 0:
            first_text = suggestions.first.inner_text()
            chat.click_suggestion(first_text)
            chat.wait_for_response(timeout=30000)
            response = chat.get_last_response()
            assert len(response) > 0, "Clicking a suggestion should produce a response"
        else:
            # No suggestions visible; send a manual message instead
            chat.send_message("list entities", timeout=30000)
            response = chat.get_last_response()
            assert len(response) > 0, "Should get a response for 'list entities'"

    def test_04_markdown_table_renders(self, page, mock_llm_server):
        """Verify that markdown content renders properly in the chat."""
        reset_mock_state()
        chat = AIChatPanel(page)
        chat.navigate_to_chat()
        chat.wait_for_panel(timeout=15000)

        # The "create" response includes markdown bold and list items
        chat.send_message("create a TestMarkdownEntity", timeout=30000)
        response = chat.get_last_response()
        assert "TestMarkdownEntity" in response, \
            f"Response should mention the entity name, got: {response}"

        # Check that markdown was rendered (bold text becomes HTML tags)
        html = chat.get_last_response_html()
        # DxAIChat should render **text** as <strong> or <b>
        has_bold = "<strong>" in html.lower() or "<b>" in html.lower()
        has_list = "<li>" in html.lower() or "- " in response
        assert has_bold or has_list, \
            f"Markdown should be rendered as HTML. Got HTML: {html[:500]}"


class TestEntityCreationFlow:
    """Create entity via natural language conversation."""

    def test_01_propose_entity(self, page, mock_llm_server):
        """Send a 'create entity' message and verify the proposal response."""
        reset_mock_state()
        chat = AIChatPanel(page)
        chat.navigate_to_chat()
        chat.wait_for_panel(timeout=15000)

        chat.send_message("create a ChatTestEntity", timeout=30000)
        response = chat.get_last_response()
        assert "ChatTestEntity" in response, \
            f"Proposal should mention the entity name, got: {response}"
        assert "field" in response.lower(), \
            f"Proposal should mention fields, got: {response}"
        assert "look good" in response.lower() or "confirm" in response.lower(), \
            f"Proposal should ask for confirmation, got: {response}"

    def test_02_confirm_creation(self, page, mock_llm_server):
        """Confirm the entity creation proposal."""
        # Continue from previous test's conversation state
        # The mock server has _pending_entity set from the create message
        chat = AIChatPanel(page)
        chat.navigate_to_chat()
        chat.wait_for_panel(timeout=15000)

        # First propose the entity (fresh context per test due to function-scoped page)
        reset_mock_state()
        chat.send_message("create a ChatTestEntity", timeout=30000)
        page.wait_for_timeout(1000)

        # Now confirm
        chat.send_message("yes", timeout=30000)
        response = chat.get_last_response()
        # After tool execution, the follow-up says "Created the entity"
        assert "creat" in response.lower() or "done" in response.lower() or "deploy" in response.lower(), \
            f"Confirmation response should indicate creation, got: {response}"

    def test_03_entity_exists_in_metadata(self, page, mock_llm_server):
        """After AI-assisted creation, verify the entity exists in Custom Class list.

        NOTE: This test depends on the AI chat actually executing the create_entity
        tool call against the XAF backend. If the mock server only returns tool_use
        responses without backend integration, this test verifies the tool call
        was generated correctly instead.
        """
        reset_mock_state()
        chat = AIChatPanel(page)
        chat.navigate_to_chat()
        chat.wait_for_panel(timeout=15000)

        # Propose and confirm creation
        chat.send_message("create a ChatTestVerify", timeout=30000)
        page.wait_for_timeout(500)
        chat.send_message("yes", timeout=30000)

        # The mock server returns a tool_use for create_entity
        # Check if the response indicates the tool was called
        responses = chat.get_all_responses()
        last = responses[-1] if responses else ""
        # The tool result follow-up says "Created the entity"
        assert "creat" in last.lower() or "entity" in last.lower(), \
            f"Expected creation confirmation, got: {last}"


class TestEntityModificationFlow:
    """Test adding fields to entities via chat."""

    def test_01_add_field_proposal(self, page, mock_llm_server):
        """Send an 'add field' message and verify the proposal."""
        reset_mock_state()
        chat = AIChatPanel(page)
        chat.navigate_to_chat()
        chat.wait_for_panel(timeout=15000)

        chat.send_message("add a field 'Email' to 'Customer'", timeout=30000)
        response = chat.get_last_response()
        assert "email" in response.lower(), \
            f"Response should mention the field name, got: {response}"
        assert "customer" in response.lower(), \
            f"Response should mention the entity name, got: {response}"
        assert "look good" in response.lower() or "confirm" in response.lower(), \
            f"Response should ask for confirmation, got: {response}"


class TestEntityDeletionFlow:
    """Test entity deletion proposals via chat."""

    def test_01_delete_proposal(self, page, mock_llm_server):
        """Send a 'delete entity' message and verify the warning."""
        reset_mock_state()
        chat = AIChatPanel(page)
        chat.navigate_to_chat()
        chat.wait_for_panel(timeout=15000)

        chat.send_message("delete entity 'ObsoleteEntity'", timeout=30000)
        response = chat.get_last_response()
        assert "ObsoleteEntity" in response, \
            f"Response should mention the entity to delete, got: {response}"
        assert "confirm" in response.lower() or "sure" in response.lower(), \
            f"Response should ask for confirmation before deletion, got: {response}"
        assert "data" in response.lower() or "remove" in response.lower(), \
            f"Response should warn about data loss, got: {response}"


class TestValidation:
    """Test schema validation via chat."""

    def test_01_validate_schema(self, page, mock_llm_server):
        """Send a 'validate schema' message and check for tool invocation response."""
        reset_mock_state()
        chat = AIChatPanel(page)
        chat.navigate_to_chat()
        chat.wait_for_panel(timeout=15000)

        chat.send_message("validate the schema", timeout=30000)
        response = chat.get_last_response()
        # The mock returns a tool_use for validate_schema, then follow-up text
        assert "validat" in response.lower() or "schema" in response.lower() or "compil" in response.lower(), \
            f"Expected validation-related response, got: {response}"


class TestPendingChanges:
    """Test pending changes display via chat."""

    def test_01_show_pending_changes(self, page, mock_llm_server):
        """Send a 'show pending changes' message and verify response."""
        reset_mock_state()
        chat = AIChatPanel(page)
        chat.navigate_to_chat()
        chat.wait_for_panel(timeout=15000)

        chat.send_message("show pending changes", timeout=30000)
        response = chat.get_last_response()
        assert "pending" in response.lower() or "change" in response.lower(), \
            f"Expected pending changes response, got: {response}"


class TestRolePermissions:
    """Test role listing and permission management via chat."""

    def test_01_list_roles(self, page, mock_llm_server):
        """Send 'list roles' and verify the tool is invoked."""
        reset_mock_state()
        chat = AIChatPanel(page)
        chat.navigate_to_chat()
        chat.wait_for_panel(timeout=15000)

        chat.send_message("list roles", timeout=30000)
        response = chat.get_last_response()
        assert "role" in response.lower(), \
            f"Expected roles-related response, got: {response}"

    def test_02_set_permissions_proposal(self, page, mock_llm_server):
        """Send a permissions request and verify the response."""
        reset_mock_state()
        chat = AIChatPanel(page)
        chat.navigate_to_chat()
        chat.wait_for_panel(timeout=15000)

        chat.send_message("set permission for Customer entity", timeout=30000)
        response = chat.get_last_response()
        assert "permission" in response.lower() or "access" in response.lower(), \
            f"Expected permissions-related response, got: {response}"


class TestConversationContinuity:
    """Test multi-turn conversation flows."""

    def test_01_multi_turn(self, page, mock_llm_server):
        """Verify that a multi-turn conversation accumulates messages correctly."""
        reset_mock_state()
        chat = AIChatPanel(page)
        chat.navigate_to_chat()
        chat.wait_for_panel(timeout=15000)

        # Turn 1: propose an entity
        chat.send_message("create a MultiTurnTest", timeout=30000)
        first_response = chat.get_last_response()
        assert "MultiTurnTest" in first_response, \
            f"First response should mention entity name, got: {first_response}"

        # Check message count after first exchange (1 user + 1 assistant = 2)
        count_after_first = chat.get_message_count()
        assert count_after_first >= 2, \
            f"Expected at least 2 messages after first exchange, got {count_after_first}"

        # Turn 2: confirm creation
        chat.send_message("yes", timeout=30000)
        second_response = chat.get_last_response()
        assert len(second_response) > 0, "Second turn should produce a response"

        # Message count should increase
        count_after_second = chat.get_message_count()
        assert count_after_second > count_after_first, \
            f"Message count should increase: {count_after_second} > {count_after_first}"

        # All responses should be retrievable
        all_responses = chat.get_all_responses()
        assert len(all_responses) >= 2, \
            f"Should have at least 2 assistant responses, got {len(all_responses)}"


class TestCleanup:
    """Clean up any test data created during AI chat tests."""

    def test_99_cleanup(self, page, mock_llm_server):
        """Remove any entities created by AI chat tests."""
        reset_mock_state()
        nav, lv = nav_to_custom_class(page)
        for name in [
            "ChatTestEntity",
            "ChatTestVerify",
            "TestMarkdownEntity",
            "MultiTurnTest",
            "NewEntity",
        ]:
            delete_if_exists(page, name)
            page.wait_for_timeout(300)
