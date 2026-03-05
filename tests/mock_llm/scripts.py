"""Pre-scripted response matching for the mock LLM server.

Each script entry is a tuple of (keyword_matcher, response_builder).
The matcher receives the latest user message text (lowercased) and returns
True/False.  The response builder receives the original (non-lowered) message
and returns a dict that the server translates into the appropriate wire format.

Response dict shapes:
  {"type": "text", "text": "..."}
  {"type": "tool_use", "name": "...", "input": {...}}
"""

from __future__ import annotations

import re
from typing import Any, Callable

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _text(msg: str) -> dict:
    return {"type": "text", "text": msg}


def _tool(name: str, args: dict[str, Any]) -> dict:
    return {"type": "tool_use", "name": name, "input": args}


def _extract_entity_name(text: str) -> str:
    """Try to pull an entity/class name from the user message."""
    # Look for quoted names first
    m = re.search(r'["\'](\w+)["\']', text)
    if m:
        return m.group(1)
    # Look for "called X" or "named X"
    m = re.search(r'(?:called|named)\s+(\w+)', text, re.IGNORECASE)
    if m:
        return m.group(1)
    # Look for "create X" pattern - take the word after create
    m = re.search(r'create\s+(?:a\s+|an\s+)?(\w+)', text, re.IGNORECASE)
    if m:
        name = m.group(1)
        # Skip generic words
        if name.lower() not in ("a", "an", "the", "new", "entity", "class"):
            return name.capitalize()
    return "NewEntity"


def _extract_field_name(text: str) -> str:
    """Try to pull a field name from an 'add field' message."""
    m = re.search(r'field\s+["\']?(\w+)["\']?', text, re.IGNORECASE)
    if m:
        return m.group(1)
    m = re.search(r'add\s+(?:a\s+)?(\w+)\s+field', text, re.IGNORECASE)
    if m:
        return m.group(1)
    return "NewField"


# ---------------------------------------------------------------------------
# Conversation state (simple per-process state for multi-turn flows)
# ---------------------------------------------------------------------------

_pending_entity: dict[str, Any] | None = None


def reset_state() -> None:
    """Reset conversation state (call between test scenarios)."""
    global _pending_entity
    _pending_entity = None


# ---------------------------------------------------------------------------
# Script entries: (matcher, builder)
# ---------------------------------------------------------------------------

def _match_confirm(text: str) -> bool:
    return text.strip().lower() in ("yes", "y", "confirm", "looks good", "lgtm", "sure", "ok", "do it")


def _build_confirm(_text: str) -> dict:
    global _pending_entity
    if _pending_entity:
        entity = _pending_entity
        _pending_entity = None
        return _tool("create_entity", entity)
    # No pending entity - generic confirm
    return _text_resp("OK, confirmed.")


def _match_create(text: str) -> bool:
    return "create" in text and not _match_confirm(text)


def _build_create(text: str) -> dict:
    global _pending_entity
    name = _extract_entity_name(text)
    # Build a default set of fields based on common patterns
    fields = [
        {"field_name": "Name", "type_name": "System.String"},
        {"field_name": "Description", "type_name": "System.String"},
    ]
    _pending_entity = {
        "class_name": name,
        "fields": fields,
        "navigation_group": "Default",
    }
    return _text(
        f"I'll create a **{name}** entity with these fields:\n"
        f"- Name (string)\n"
        f"- Description (string)\n\n"
        f"Look good?"
    )


def _match_list_entities(text: str) -> bool:
    return "list" in text and "entit" in text


def _build_list_entities(_text: str) -> dict:
    return _tool("list_entities", {})


def _match_describe(text: str) -> bool:
    return ("describe" in text or "show" in text) and "field" in text


def _build_describe(text: str) -> dict:
    name = _extract_entity_name(text)
    return _tool("describe_entity", {"class_name": name})


def _match_pending(text: str) -> bool:
    return "pending" in text or "changes" in text


def _build_pending(_text: str) -> dict:
    return _tool("get_pending_changes", {})


def _match_add_field(text: str) -> bool:
    return "add" in text and "field" in text


def _build_add_field(text: str) -> dict:
    field_name = _extract_field_name(text)
    entity_name = _extract_entity_name(text)
    return _text(
        f"I'll add a **{field_name}** field (string) to **{entity_name}**.\n\n"
        f"Look good?"
    )


def _match_delete(text: str) -> bool:
    return "delete" in text or "remove" in text


def _build_delete(text: str) -> dict:
    name = _extract_entity_name(text)
    return _text(
        f"Are you sure you want to delete **{name}**? "
        f"This will remove the entity and all its data. "
        f"Type 'confirm' to proceed."
    )


def _match_list_roles(text: str) -> bool:
    return "list" in text and "role" in text


def _build_list_roles(_text: str) -> dict:
    return _tool("list_roles", {})


def _match_permissions(text: str) -> bool:
    return "permission" in text or "access" in text


def _build_permissions(text: str) -> dict:
    return _text(
        "I can update permissions for an entity. "
        "Which entity and role would you like to modify access for?"
    )


def _match_validate(text: str) -> bool:
    return "validate" in text or "compile" in text


def _build_validate(_text: str) -> dict:
    return _tool("validate_schema", {})


def _match_tool_result(text: str) -> bool:
    """Match messages that look like tool results (after tool execution)."""
    return False  # Tool results are handled at the server level


def _build_tool_result_followup(tool_name: str, _result: str) -> dict:
    """Build a follow-up response after a tool result."""
    followups = {
        "create_entity": "Created the entity. Click Deploy when ready.",
        "list_entities": "Here are the current entities in the system.",
        "describe_entity": "Here are the fields for the entity.",
        "get_pending_changes": "Here are the pending changes.",
        "list_roles": "Here are the available roles.",
        "validate_schema": "Schema validation complete.",
    }
    return _text(followups.get(tool_name, "Done."))


# ---------------------------------------------------------------------------
# Ordered script table — first match wins
# ---------------------------------------------------------------------------

SCRIPTS: list[tuple[Callable[[str], bool], Callable[[str], dict]]] = [
    (_match_confirm, _build_confirm),
    (_match_list_entities, _build_list_entities),
    (_match_list_roles, _build_list_roles),
    (_match_describe, _build_describe),
    (_match_pending, _build_pending),
    (_match_add_field, _build_add_field),
    (_match_delete, _build_delete),
    (_match_permissions, _build_permissions),
    (_match_validate, _build_validate),
    (_match_create, _build_create),
]

DEFAULT_RESPONSE = _text(
    "I can help you create, modify, or delete entities. "
    "What would you like to do?"
)


def _text_resp(msg: str) -> dict:
    return _text(msg)


def match(user_message: str) -> dict:
    """Match user_message against scripts and return a response dict."""
    lower = user_message.lower()
    for matcher, builder in SCRIPTS:
        if matcher(lower):
            return builder(user_message)
    return DEFAULT_RESPONSE
