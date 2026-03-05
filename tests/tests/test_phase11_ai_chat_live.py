"""Phase 11 Tests (Live): AI Chat with real LLM backend.

These tests require a live AI API key and make real calls to the LLM provider.
They are skipped unless the AI_TEST_API_KEY environment variable is set.

All tests use 60-second timeouts since real AI responses can be slow.
Entity names include random suffixes to avoid conflicts between test runs.
"""
import os
import uuid
import pytest
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from pages.ai_chat_page import AIChatPanel
from pages.navigation_page import NavigationPage
from pages.list_view_page import ListViewPage

# Skip entire module if no API key is configured
if not os.environ.get("AI_TEST_API_KEY"):
    pytest.skip(
        "AI_TEST_API_KEY environment variable not set — skipping live AI tests",
        allow_module_level=True,
    )

# Longer timeout for real AI calls
AI_TIMEOUT = 60000


def unique_name(prefix: str = "AITest") -> str:
    """Generate a unique entity name to avoid conflicts."""
    return f"{prefix}{uuid.uuid4().hex[:6]}"


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


def cleanup_entity(page, name):
    """Navigate to Custom Class list and delete entity by name if it exists."""
    try:
        nav, lv = nav_to_custom_class(page)
        delete_if_exists(page, name)
    except Exception:
        pass  # Best-effort cleanup


@pytest.mark.live_ai
class TestLiveEntityCreation:
    """Test creating entities through natural language conversation with a real LLM."""

    def test_01_create_entity_natural_language(self, page):
        """Ask the AI to create an entity, confirm the action, and verify it appears
        in the CustomClass list view."""
        entity_name = unique_name("AICreate")
        chat = AIChatPanel(page)
        chat.navigate_to_chat()
        chat.wait_for_panel(timeout=15000)

        # Ask the AI to create an entity
        chat.send_message(
            f"Create a new entity called {entity_name} with fields: "
            f"Name (string), Age (integer), Email (string). "
            f"Put it in the Testing navigation group.",
            timeout=AI_TIMEOUT,
        )

        # The AI should respond with something about creating the entity
        response = chat.get_last_response()
        assert len(response) > 0, "AI returned empty response"

        # Check the response mentions the entity name (case-insensitive)
        assert entity_name.lower() in response.lower(), (
            f"AI response does not mention entity name '{entity_name}': {response[:200]}"
        )

        # If the AI asks for confirmation, confirm
        if "?" in response or "confirm" in response.lower():
            chat.send_message("Yes, please create it.", timeout=AI_TIMEOUT)

        # Verify the entity appears in the CustomClass list
        try:
            nav, lv = nav_to_custom_class(page)
            page.wait_for_timeout(2000)
            assert lv.has_row_with_text(entity_name), (
                f"Entity '{entity_name}' not found in Custom Class list after AI creation"
            )
        finally:
            cleanup_entity(page, entity_name)


@pytest.mark.live_ai
class TestLiveEntityModification:
    """Test modifying existing entities through natural language."""

    def test_01_add_field_via_chat(self, page):
        """Create an entity first, then ask the AI to add a field to it."""
        entity_name = unique_name("AIModify")
        chat = AIChatPanel(page)
        chat.navigate_to_chat()
        chat.wait_for_panel(timeout=15000)

        # First, ask the AI to create a simple entity
        chat.send_message(
            f"Create an entity called {entity_name} with a Name field (string).",
            timeout=AI_TIMEOUT,
        )
        response = chat.get_last_response()
        assert len(response) > 0, "AI returned empty response for creation"

        # Confirm if needed
        if "?" in response or "confirm" in response.lower():
            chat.send_message("Yes, go ahead.", timeout=AI_TIMEOUT)

        # Now ask to add a new field
        chat.send_message(
            f"Add a Salary field of type decimal to {entity_name}.",
            timeout=AI_TIMEOUT,
        )
        modify_response = chat.get_last_response()
        assert len(modify_response) > 0, "AI returned empty response for modification"

        # Confirm if needed
        if "?" in modify_response or "confirm" in modify_response.lower():
            chat.send_message("Yes, add it.", timeout=AI_TIMEOUT)

        # The AI should mention the field was added or the salary field
        final_response = chat.get_last_response()
        assert "salary" in final_response.lower() or "added" in final_response.lower(), (
            f"AI response does not confirm field addition: {final_response[:200]}"
        )

        # Cleanup
        cleanup_entity(page, entity_name)


@pytest.mark.live_ai
class TestLiveAmbiguityResolution:
    """Test that the AI asks clarifying questions for vague requests."""

    def test_01_vague_request(self, page):
        """Send a vague request and verify the AI asks for clarification
        rather than guessing."""
        chat = AIChatPanel(page)
        chat.navigate_to_chat()
        chat.wait_for_panel(timeout=15000)

        chat.send_message(
            "I need to track some stuff",
            timeout=AI_TIMEOUT,
        )

        response = chat.get_last_response()
        assert len(response) > 0, "AI returned empty response"

        # The AI should ask a clarifying question — look for question marks
        # or typical clarification language
        has_question = "?" in response
        has_clarification = any(
            word in response.lower()
            for word in ["what", "which", "could you", "can you", "more detail",
                         "specify", "clarify", "tell me more", "what kind"]
        )
        assert has_question or has_clarification, (
            f"AI did not ask for clarification on vague request. Response: {response[:300]}"
        )


@pytest.mark.live_ai
class TestLiveRolePermissions:
    """Test AI responses about role and permission queries."""

    def test_01_ask_about_permissions(self, page):
        """Ask the AI about roles/permissions and verify it gives a meaningful answer."""
        chat = AIChatPanel(page)
        chat.navigate_to_chat()
        chat.wait_for_panel(timeout=15000)

        chat.send_message(
            "What roles are available in the system?",
            timeout=AI_TIMEOUT,
        )

        response = chat.get_last_response()
        assert len(response) > 0, "AI returned empty response"

        # The AI should mention something about roles — either listing them
        # or explaining the role system
        has_role_content = any(
            word in response.lower()
            for word in ["role", "admin", "permission", "user", "access"]
        )
        assert has_role_content, (
            f"AI response does not contain role-related content: {response[:300]}"
        )


@pytest.mark.live_ai
class TestLiveMultiTurn:
    """Test multi-turn conversations with a real LLM."""

    def test_01_create_then_modify(self, page):
        """Full workflow: create entity, confirm, add field, confirm.
        Verify the conversation has at least 6 messages (3 user + 3 assistant)."""
        entity_name = unique_name("AIMulti")
        chat = AIChatPanel(page)
        chat.navigate_to_chat()
        chat.wait_for_panel(timeout=15000)

        # Turn 1: Ask to create an entity
        chat.send_message(
            f"Create an entity called {entity_name} with a Name field (string) "
            f"in the Testing group.",
            timeout=AI_TIMEOUT,
        )
        response1 = chat.get_last_response()
        assert len(response1) > 0, "Empty response on turn 1"

        # Turn 2: Confirm creation
        chat.send_message(
            "Yes, create it please.",
            timeout=AI_TIMEOUT,
        )
        response2 = chat.get_last_response()
        assert len(response2) > 0, "Empty response on turn 2"

        # Turn 3: Ask to modify
        chat.send_message(
            f"Now add an IsActive field (boolean) to {entity_name}.",
            timeout=AI_TIMEOUT,
        )
        response3 = chat.get_last_response()
        assert len(response3) > 0, "Empty response on turn 3"

        # Confirm modification if needed
        if "?" in response3 or "confirm" in response3.lower():
            chat.send_message("Yes, add it.", timeout=AI_TIMEOUT)

        # Verify conversation length — at least 6 messages (3 user + 3 assistant)
        msg_count = chat.get_message_count()
        assert msg_count >= 6, (
            f"Expected at least 6 messages in multi-turn conversation, got {msg_count}"
        )

        # Cleanup
        cleanup_entity(page, entity_name)
