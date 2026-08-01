# TODO — XafDynamicAssemblies

## P2: Medium

#### ACT-002: AI-chat action verbs — 4 tools for metadata actions (ID: 1139)

Give the AI schema assistant list_actions / create_action / delete_action / set_action_active
for the ACT-001 metadata actions (live DetailView buttons). Spec:
`docs/superpowers/specs/2026-07-31-ai-chat-action-verbs-design.md`; plan:
`docs/superpowers/plans/2026-07-31-ai-chat-action-verbs.md`. Implemented and verified on
`feature/act-002-ai-action-verbs` (9 commits, regression 167/1/1 — sole failure is the
pre-existing TEST-001 flake). Awaiting merge decision.

#### TEST-001: Deploy-restart navigation race in WaitForDeployRestartAsync (ID: 1140)

`ServerHelper.WaitForDeployRestartAsync` (ServerHelper.cs:45) can fail any deploy test with
"Navigation to '/' is interrupted by another navigation to '/CustomClass_ListView'" — the
post-restart auto-redirect races the helper's own `GotoAsync("/")`. Bit Phase09
Test_09_DeployAndVerify in the 2026-08-01 full regression (only failure; 19/19 green
standalone minutes later). Fix once in the shared helper: catch the interrupted-navigation
PlaywrightException and retry.

#### TEST-002: Mock LLM create_entity drift — class_name vs className (ID: 1141)

The mock's canned `create_entity` tool_use sends `class_name`/`fields` but the real tool's
parameters are `className`/`fieldsJson` — so in mocked runs create_entity ALWAYS fails
server-side and Phase 11's create-entity chat tests pass on canned follow-up text alone.
Align the mock to real parameter names (ScriptMatcher.cs + the MockLlmServerTests self-test
that pins `class_name`) and add a DB assertion to at least one create-entity test so drift
fails loudly. The ACT-002 action-verb matchers are the pattern to copy.

(Completed work: see `docs/DONE.md`. Future ideas: `BACKBURNER.md` — including the ACT-001
fast-follows: AI-chat action verbs, ListView targets, expression values.)
