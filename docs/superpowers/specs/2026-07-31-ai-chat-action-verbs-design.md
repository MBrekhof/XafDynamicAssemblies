# AI-Chat Action Verbs — Design

**Date:** 2026-07-31 · **Status:** approved (design), pending implementation
**Backburner origin:** ACT-001 fast-follow "AI-chat action verbs" (BACKBURNER.md)

## Purpose

Let the AI schema assistant create and manage **metadata actions** (`CustomAction` /
`CustomActionStep` — the live DetailView buttons shipped as ACT-001) through natural
language, the same way it already manages entities. "Add an Approve button to Order that
sets Status to Approved and shows a confirmation" becomes one chat turn.

## Scope

Four new tools in `SchemaAIToolsProvider` (10 → 14 total). No changes to the dispatcher,
entities, or validation rules. Out of scope: `modify_action` (delete + recreate covers it),
ListView targets, expression values (separate backburner items).

## Tools

All four follow the existing provider pattern exactly: private method +
`AIFunctionFactory.Create(Method, "tool_name")` in `CreateTools()`, `[Description]`
attributes on method and parameters, `ScopedObjectSpace` via
`INonSecuredObjectSpaceFactory`, markdown-string returns, per-tool logging, catch-all
`return $"Error: …"` — never throw.

### `list_actions(entityName?)`

Markdown table of all `CustomAction` rows, optionally filtered by `TargetEntity ==
entityName`. Columns: Caption | Target Entity | Active | Steps | Criteria. The Steps cell
joins each step's `DisplayText` ("1. Set Status = Approved; 2. Message: Done"). Empty
result → hint at `create_action`.

### `create_action(caption, targetEntity, criteria?, confirmationMessage?, stepsJson)`

Creates one `CustomAction` with aggregated steps. `stepsJson` is a JSON array of:

```json
{ "kind": "SetField" | "ShowMessage" | "OpenView",
  "fieldName": "...", "value": "...",          // SetField
  "messageText": "...", "messageType": "Info", // ShowMessage (Info|Success|Warning|Error, default Info)
  "targetEntityName": "..." }                  // OpenView
```

`SortOrder` is assigned from array position — the LLM never manages sort numbers. `Kind`
and `MessageType` parse case-insensitively.

**Hard errors** (nothing created) — mirrors the XAF save rules, which do NOT fire on the
non-secured tool ObjectSpace:
- missing caption or targetEntity
- duplicate (Caption, TargetEntity) — points at `delete_action` first
- empty/unparseable stepsJson, or zero steps (a no-op button is always a mistake)
- per-kind required fields missing (SetField→fieldName, ShowMessage→messageText,
  OpenView→targetEntityName); unknown `kind`
- more than one OpenView step

**Soft warnings** (created anyway, warning appended to the response) — matches the shipped
save-time-warning philosophy:
- criteria doesn't parse via `CriteriaOperator.Parse` (action will be disabled until fixed)
- targetEntity not found among runtime or compiled types (it may be created/deployed later)
- entity already has ≥ 10 active actions (dispatcher slot ceiling — overflow won't render)

Success response states the ACT-001 contract explicitly so the model relays it: **live the
next time the entity's DetailView opens — no deploy, no restart** (unlike entity changes).

### `delete_action(caption, targetEntity)`

Looks up by the natural unique key (Caption, TargetEntity). Deletes aggregated steps first,
then the action (mirrors `delete_entity`). Not-found → lists available actions for that
entity.

### `set_action_active(caption, targetEntity, isActive)`

Same lookup; flips `IsActive`, commits. Dispatcher already filters on `IsActive` per
activation, so the change is live on next DetailView open.

## System Prompt

One short capability paragraph added in `SchemaDiscoveryService.GenerateSystemPrompt`:
the assistant can add live buttons (metadata actions) to entity DetailViews via
`create_action` (SetField / ShowMessage / OpenView steps), inspect them via `list_actions`,
and remove/toggle them — changes are live without deploy. No upfront action list in the
prompt — `list_actions` is the on-demand tier, consistent with the two-tier prompt design.

## Mock LLM (ScriptMatcher)

One self-contained block right after the confirmation check — BEFORE the generic
`delete`/`create` matchers, which would otherwise shadow "delete the Approve action" /
"create an action":

```
if message contains "action" or "button":
    "list"                                   → tool_use list_actions
    "delete" / "remove"                      → tool_use delete_action  (canned key)
    "disable"/"enable"/"activate"/"deactivate" → tool_use set_action_active
    "add" / "create"                         → tool_use create_action  (canned steps payload)
```

Plus `ToolResultFollowups` entries for all four tools ("Action created — it appears the
next time the …" etc.).

## Testing (Phase 11 extension)

Three new mocked E2E tests (`Test_16`–`Test_18`, keeping alphabetical order):

1. **Create via chat** — send "add a … button/action", assert response mentions the action
   and liveness; then `list_actions` via chat shows it.
2. **Toggle + delete via chat** — disable via chat, verify `list_actions` shows Inactive;
   delete via chat, verify gone. (Cleanup doubles as coverage — no fourth test.)
3. **Integration seam** — after create-via-chat, open the target entity's DetailView and
   assert the slot button renders (reuse Phase 12's page helpers and its target-entity
   choice so Phase 11 gains no new cross-suite dependency).

The mock's canned `create_action` payload targets the same entity Phase 12 uses.
StepValueConverter and dispatcher behavior are already unit/E2E-covered — not retested.

## Docs

README + CLAUDE.md: tool count 10 → 14 and one line in the AI Schema Assistant section.
DONE/TODO/board per the normal task lifecycle (card minted at implementation start).
