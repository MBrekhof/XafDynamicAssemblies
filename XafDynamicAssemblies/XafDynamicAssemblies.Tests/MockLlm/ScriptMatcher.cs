using System.Text.RegularExpressions;

namespace XafDynamicAssemblies.Tests.MockLlm;

/// <summary>
/// Deterministic pre-scripted response matching for the mock LLM server.
/// Ported 1:1 from tests/mock_llm/scripts.py — matcher order, wording, and tool
/// input shapes intentionally mirror the Python source so wire responses stay
/// byte-identical for consumers (e.g. the Anthropic/OpenAI SDK glue in AIChatClient).
/// </summary>
public class ScriptMatcher
{
    // Python: text.strip().lower() in (...) — exact match, not substring.
    private static readonly HashSet<string> ConfirmWords = new(StringComparer.Ordinal)
    {
        "yes", "y", "confirm", "looks good", "lgtm", "sure", "ok", "do it",
    };

    // Python: name.lower() not in (...) guard in _extract_entity_name.
    private static readonly HashSet<string> GenericEntityWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "new", "entity", "class",
    };

    // Python: _build_tool_result_followup's `followups` dict.
    private static readonly Dictionary<string, string> ToolResultFollowups = new()
    {
        ["create_entity"] = "Created the entity. Click Deploy when ready.",
        ["list_entities"] = "Here are the current entities in the system.",
        ["describe_entity"] = "Here are the fields for the entity.",
        ["get_pending_changes"] = "Here are the pending changes.",
        ["list_roles"] = "Here are the available roles.",
        ["validate_schema"] = "Schema validation complete.",
        ["list_actions"] = "Here are the metadata actions.",
        ["create_action"] = "Action created — the button appears the next time the entity's detail view opens. No deploy needed.",
        ["delete_action"] = "Action deleted.",
        ["set_action_active"] = "Action active state updated.",
    };

    private Dictionary<string, object>? _pendingEntity;

    public void Reset() => _pendingEntity = null;

    /// <summary>
    /// Mirrors scripts.py's SCRIPTS ordered table — first match wins. Order matters:
    /// confirm > list_entities > list_roles > describe > pending > add_field > delete
    /// > permissions > validate > create > default.
    /// </summary>
    public Dictionary<string, object> Match(string userMessage)
    {
        var lower = userMessage.ToLowerInvariant();

        if (IsConfirmation(lower)) return BuildConfirm();

        // Metadata action verbs. MUST run before the generic delete/create matchers below,
        // which would otherwise shadow "delete the Approve action" / "create an action".
        if (lower.Contains("action") || lower.Contains("button"))
        {
            if (lower.Contains("list") || lower.Contains("what"))
                return ToolUse("list_actions", new Dictionary<string, object> { ["entityName"] = "" });
            if (lower.Contains("disable") || lower.Contains("deactivate") || lower.Contains("enable") || lower.Contains("activate"))
                return ToolUse("set_action_active", new Dictionary<string, object>
                {
                    ["caption"] = "Approve",
                    ["targetEntity"] = ExtractEntityName(userMessage),
                    // NOTE: "deactivate".Contains("activate") is true — decide by the negative words.
                    ["isActive"] = !(lower.Contains("disable") || lower.Contains("deactivate")),
                });
            if (lower.Contains("delete") || lower.Contains("remove"))
                return ToolUse("delete_action", new Dictionary<string, object>
                {
                    ["caption"] = "Approve",
                    ["targetEntity"] = ExtractEntityName(userMessage),
                });
            if (lower.Contains("add") || lower.Contains("create"))
                return ToolUse("create_action", new Dictionary<string, object>
                {
                    ["caption"] = "Approve",
                    ["targetEntity"] = ExtractEntityName(userMessage),
                    ["criteria"] = "",
                    ["confirmationMessage"] = "",
                    ["stepsJson"] = "[{\"kind\":\"SetField\",\"fieldName\":\"Status\",\"value\":\"Approved\"},{\"kind\":\"ShowMessage\",\"messageText\":\"Approved via chat\",\"messageType\":\"Success\"}]",
                });
            // No verb matched — fall through to the generic rules.
        }

        if (lower.Contains("list") && lower.Contains("entit")) return ToolUse("list_entities", EmptyInput());
        if (lower.Contains("list") && lower.Contains("role")) return ToolUse("list_roles", EmptyInput());
        if ((lower.Contains("describe") || lower.Contains("show")) && lower.Contains("field"))
            return ToolUse("describe_entity", new Dictionary<string, object> { ["class_name"] = ExtractEntityName(userMessage) });
        if (lower.Contains("pending") || lower.Contains("changes")) return ToolUse("get_pending_changes", EmptyInput());
        if (lower.Contains("add") && lower.Contains("field")) return BuildAddField(userMessage);
        if (lower.Contains("delete") || lower.Contains("remove")) return BuildDelete(userMessage);
        if (lower.Contains("permission") || lower.Contains("access")) return BuildPermissions();
        if (lower.Contains("validate") || lower.Contains("compile")) return ToolUse("validate_schema", EmptyInput());
        if (lower.Contains("create")) return BuildCreate(userMessage);

        return Text("I can help you create, modify, or delete entities. What would you like to do?");
    }

    /// <summary>Port of scripts.py's _build_tool_result_followup — always text, never null.</summary>
    public Dictionary<string, object> MatchToolResult(string toolName) =>
        Text(ToolResultFollowups.TryGetValue(toolName, out var msg) ? msg : "Done.");

    private Dictionary<string, object> BuildConfirm()
    {
        if (_pendingEntity is { } entity)
        {
            _pendingEntity = null;
            return ToolUse("create_entity", entity);
        }
        return Text("OK, confirmed.");
    }

    private Dictionary<string, object> BuildCreate(string text)
    {
        var name = ExtractEntityName(text);
        // Deviation from scripts.py (TEST-002): the Python shape (class_name/fields/
        // navigation_group) never matched the real create_entity tool's C# parameter names
        // (className/navigationGroup/description/fieldsJson), so the tool errored on every
        // mocked confirm while tests passed on canned follow-up text. Keys below are the
        // real AIFunction parameter names — same contract the action-verb matchers use.
        _pendingEntity = new Dictionary<string, object>
        {
            ["className"] = name,
            ["navigationGroup"] = "Default",
            ["description"] = "",
            ["fieldsJson"] =
                "[{\"name\":\"Name\",\"type\":\"System.String\"}," +
                "{\"name\":\"Description\",\"type\":\"System.String\"}]",
        };
        return Text(
            $"I'll create a **{name}** entity with these fields:\n" +
            "- Name (string)\n" +
            "- Description (string)\n\n" +
            "Look good?");
    }

    private static Dictionary<string, object> BuildAddField(string text)
    {
        var fieldName = ExtractFieldName(text);
        var entityName = ExtractEntityName(text);
        return Text($"I'll add a **{fieldName}** field (string) to **{entityName}**.\n\nLook good?");
    }

    private static Dictionary<string, object> BuildDelete(string text)
    {
        var name = ExtractEntityName(text);
        return Text(
            $"Are you sure you want to delete **{name}**? " +
            "This will remove the entity and all its data. " +
            "Type 'confirm' to proceed.");
    }

    private static Dictionary<string, object> BuildPermissions() =>
        Text("I can update permissions for an entity. Which entity and role would you like to modify access for?");

    private static bool IsConfirmation(string lowerMessage) => ConfirmWords.Contains(lowerMessage.Trim());

    private static string ExtractEntityName(string text)
    {
        // Deviation from scripts.py: take the LAST quoted word, not the first. Python's
        // re.search (and the original Regex.Match here) always grabs the leftmost quoted
        // token, so "add a field 'Email' to 'Customer'" resolved the *entity* name to
        // "Email" (the field name) instead of "Customer" — a bug in the reference
        // implementation that breaks Test_08 (AddFieldProposal)'s "customer" assertion.
        var quoted = Regex.Matches(text, @"[""'](\w+)[""']");
        if (quoted.Count > 0) return quoted[^1].Groups[1].Value;

        var named = Regex.Match(text, @"(?:called|named)\s+(\w+)", RegexOptions.IgnoreCase);
        if (named.Success) return named.Groups[1].Value;

        var created = Regex.Match(text, @"create\s+(?:a\s+|an\s+)?(\w+)", RegexOptions.IgnoreCase);
        if (created.Success)
        {
            var name = created.Groups[1].Value;
            if (!GenericEntityWords.Contains(name.ToLowerInvariant()))
                return Capitalize(name);
        }

        return "NewEntity";
    }

    private static string ExtractFieldName(string text)
    {
        var field = Regex.Match(text, @"field\s+[""']?(\w+)[""']?", RegexOptions.IgnoreCase);
        if (field.Success) return field.Groups[1].Value;

        var addField = Regex.Match(text, @"add\s+(?:a\s+)?(\w+)\s+field", RegexOptions.IgnoreCase);
        if (addField.Success) return addField.Groups[1].Value;

        return "NewField";
    }

    /// <summary>
    /// Deviation from scripts.py: uppercase only the first character, leave the rest as-is.
    /// Python's str.capitalize() also lowercases the remainder, which mangles PascalCase
    /// input ("TestMarkdownEntity" -> "Testmarkdownentity") — a bug in the reference
    /// implementation that breaks Test_04/05/14's exact-name assertions.
    /// </summary>
    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static Dictionary<string, object> EmptyInput() => new();

    private static Dictionary<string, object> Text(string text) =>
        new() { ["type"] = "text", ["text"] = text };

    private static Dictionary<string, object> ToolUse(string name, object input) =>
        new() { ["type"] = "tool_use", ["name"] = name, ["input"] = input };
}
