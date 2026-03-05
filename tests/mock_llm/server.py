"""Mock LLM server for deterministic testing.

Supports both Anthropic Messages API (/v1/messages) and OpenAI Chat
Completions API (/v1/chat/completions) wire formats.

Usage:
    python server.py [--port 5555]

The server returns pre-scripted responses based on keyword matching
against the latest user message.  See scripts.py for the matching logic.
"""

from __future__ import annotations

import argparse
import json
import os
import uuid

from flask import Flask, jsonify, request

from scripts import match, reset_state, _build_tool_result_followup

app = Flask(__name__)

_counter = 0


def _next_id() -> str:
    global _counter
    _counter += 1
    return f"mock-{_counter}"


def _tool_call_id() -> str:
    return f"call_{uuid.uuid4().hex[:12]}"


# ---------------------------------------------------------------------------
# Anthropic Messages format helpers
# ---------------------------------------------------------------------------

def _anthropic_text_response(text: str) -> dict:
    return {
        "id": _next_id(),
        "type": "message",
        "role": "assistant",
        "model": "mock-model",
        "content": [{"type": "text", "text": text}],
        "stop_reason": "end_turn",
        "stop_sequence": None,
        "usage": {"input_tokens": 10, "output_tokens": len(text) // 4},
    }


def _anthropic_tool_response(name: str, tool_input: dict) -> dict:
    return {
        "id": _next_id(),
        "type": "message",
        "role": "assistant",
        "model": "mock-model",
        "content": [
            {
                "type": "tool_use",
                "id": _tool_call_id(),
                "name": name,
                "input": tool_input,
            }
        ],
        "stop_reason": "tool_use",
        "stop_sequence": None,
        "usage": {"input_tokens": 10, "output_tokens": 20},
    }


# ---------------------------------------------------------------------------
# OpenAI Chat Completions format helpers
# ---------------------------------------------------------------------------

def _openai_text_response(text: str) -> dict:
    return {
        "id": _next_id(),
        "object": "chat.completion",
        "model": "mock-model",
        "choices": [
            {
                "index": 0,
                "message": {
                    "role": "assistant",
                    "content": text,
                },
                "finish_reason": "stop",
            }
        ],
        "usage": {
            "prompt_tokens": 10,
            "completion_tokens": len(text) // 4,
            "total_tokens": 10 + len(text) // 4,
        },
    }


def _openai_tool_response(name: str, tool_input: dict) -> dict:
    return {
        "id": _next_id(),
        "object": "chat.completion",
        "model": "mock-model",
        "choices": [
            {
                "index": 0,
                "message": {
                    "role": "assistant",
                    "content": None,
                    "tool_calls": [
                        {
                            "id": _tool_call_id(),
                            "type": "function",
                            "function": {
                                "name": name,
                                "arguments": json.dumps(tool_input),
                            },
                        }
                    ],
                },
                "finish_reason": "tool_calls",
            }
        ],
        "usage": {
            "prompt_tokens": 10,
            "completion_tokens": 20,
            "total_tokens": 30,
        },
    }


# ---------------------------------------------------------------------------
# Message extraction
# ---------------------------------------------------------------------------

def _extract_last_user_message_anthropic(body: dict) -> str | None:
    """Extract the last user message from Anthropic Messages format."""
    messages = body.get("messages", [])
    # Walk backwards to find latest user message (skip tool_result messages)
    for msg in reversed(messages):
        if msg.get("role") == "user":
            content = msg.get("content", "")
            if isinstance(content, str):
                return content
            if isinstance(content, list):
                # Could be [{"type": "text", "text": "..."}, ...]
                # or [{"type": "tool_result", ...}]
                for block in content:
                    if isinstance(block, dict):
                        if block.get("type") == "text":
                            return block.get("text", "")
                        if block.get("type") == "tool_result":
                            # This is a tool result - return the tool name for follow-up
                            return None
                    elif isinstance(block, str):
                        return block
    return None


def _has_tool_result_anthropic(body: dict) -> str | None:
    """Check if the latest user message is a tool_result and return the tool name."""
    messages = body.get("messages", [])
    for msg in reversed(messages):
        if msg.get("role") == "user":
            content = msg.get("content", "")
            if isinstance(content, list):
                for block in content:
                    if isinstance(block, dict) and block.get("type") == "tool_result":
                        # Find the tool name from the preceding assistant message
                        tool_use_id = block.get("tool_use_id")
                        for prev_msg in reversed(messages):
                            if prev_msg.get("role") == "assistant":
                                prev_content = prev_msg.get("content", [])
                                if isinstance(prev_content, list):
                                    for prev_block in prev_content:
                                        if (isinstance(prev_block, dict)
                                                and prev_block.get("type") == "tool_use"
                                                and prev_block.get("id") == tool_use_id):
                                            return prev_block.get("name")
                        return "unknown_tool"
            break
    return None


def _extract_last_user_message_openai(body: dict) -> str | None:
    """Extract the last user message from OpenAI format."""
    messages = body.get("messages", [])
    for msg in reversed(messages):
        if msg.get("role") == "user":
            return msg.get("content", "")
        if msg.get("role") == "tool":
            # Tool result - find preceding tool call
            return None
    return None


def _has_tool_result_openai(body: dict) -> str | None:
    """Check if there's a tool result and return the function name."""
    messages = body.get("messages", [])
    for msg in reversed(messages):
        if msg.get("role") == "tool":
            # Find the tool name from the preceding assistant tool_call
            tool_call_id = msg.get("tool_call_id")
            for prev_msg in reversed(messages):
                if prev_msg.get("role") == "assistant":
                    for tc in prev_msg.get("tool_calls", []):
                        if tc.get("id") == tool_call_id:
                            return tc["function"]["name"]
            return "unknown_tool"
        if msg.get("role") in ("user", "assistant"):
            break
    return None


# ---------------------------------------------------------------------------
# Routes
# ---------------------------------------------------------------------------

@app.route("/health", methods=["GET"])
def health():
    return jsonify({"status": "ok"}), 200


@app.route("/reset", methods=["POST"])
def reset():
    """Reset conversation state between test scenarios."""
    reset_state()
    return jsonify({"status": "reset"}), 200


@app.route("/v1/messages", methods=["POST"])
def anthropic_messages():
    """Anthropic Messages API endpoint."""
    body = request.get_json(force=True)

    # Check for tool result first
    tool_name = _has_tool_result_anthropic(body)
    if tool_name:
        resp = _build_tool_result_followup(tool_name, "")
        return jsonify(_anthropic_text_response(resp["text"]))

    user_msg = _extract_last_user_message_anthropic(body)
    if not user_msg:
        return jsonify(_anthropic_text_response("I didn't catch that. Could you rephrase?"))

    result = match(user_msg)

    if result["type"] == "text":
        return jsonify(_anthropic_text_response(result["text"]))
    elif result["type"] == "tool_use":
        return jsonify(_anthropic_tool_response(result["name"], result["input"]))

    return jsonify(_anthropic_text_response("I'm not sure how to help with that."))


@app.route("/v1/chat/completions", methods=["POST"])
def openai_completions():
    """OpenAI Chat Completions API endpoint."""
    body = request.get_json(force=True)

    # Check for tool result first
    tool_name = _has_tool_result_openai(body)
    if tool_name:
        resp = _build_tool_result_followup(tool_name, "")
        return jsonify(_openai_text_response(resp["text"]))

    user_msg = _extract_last_user_message_openai(body)
    if not user_msg:
        return jsonify(_openai_text_response("I didn't catch that. Could you rephrase?"))

    result = match(user_msg)

    if result["type"] == "text":
        return jsonify(_openai_text_response(result["text"]))
    elif result["type"] == "tool_use":
        return jsonify(_openai_tool_response(result["name"], result["input"]))

    return jsonify(_openai_text_response("I'm not sure how to help with that."))


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Mock LLM server for testing")
    parser.add_argument("--port", type=int, default=int(os.environ.get("MOCK_LLM_PORT", "5555")))
    parser.add_argument("--host", default="127.0.0.1")
    args = parser.parse_args()

    print(f"Mock LLM server starting on {args.host}:{args.port}")
    app.run(host=args.host, port=args.port, debug=False)
