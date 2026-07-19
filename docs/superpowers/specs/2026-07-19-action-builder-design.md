# ACT-001: Metadata-Driven Action Builder — Design

Date: 2026-07-19 · Board card 1052 · Status: approved

## Purpose

Let admins add behavior (buttons) to runtime entities without writing code: an `Approve`
button on `Order` that sets `Status = "Approved"` and shows a message, defined entirely as
metadata. This is the constrained, safe alternative to free-form scripted ViewControllers
(BACKBURNER.md): no user C# runs in-process, no compilation, no restart.

## Decisions (user-approved)

- v1 step vocabulary: **SetField, ShowMessage, OpenView**
- Targets: **DetailView only**
- Activation: **live** — a new/changed action appears the next time its target DetailView
  opens; no deploy, no restart
- AI-chat integration (create actions via natural language): **deferred** to a fast follow

## Non-Goals (v1)

- ListView targets / multi-select semantics
- Expression values (`Now()`, `CurrentUserName()`) — literals only
- OpenView of a *related object's* DetailView — v1 opens a named entity's ListView only
- Graduation of actions to compiled controller source
- New security roles — CustomAction editing rides the same permissions as CustomClass
- AI tools (`create_action` etc.) in SchemaAIToolsProvider

## Architecture

One compiled dispatcher controller + two compiled metadata entities. No Roslyn involvement.

```
CustomAction / CustomActionStep  (metadata tables, edited via standard XAF views)
        │  queried fresh on every DetailView activation
        ▼
MetadataActionDispatcherController : ViewController<DetailView>
        │  materializes one SimpleAction per matching CustomAction row
        ▼
Step interpreter (SetField → ShowMessage → OpenView, ordered, single commit)
```

## Data Model

`Module/BusinessObjects/CustomAction.cs` — derives `BaseObject`, `[NavigationItem("Schema Management")]`:

| Property | Type | Notes |
|---|---|---|
| Caption | string, required | Button text; unique per TargetEntity |
| TargetEntity | string, required | Entity type name (runtime or compiled); free text in v1 |
| Criteria | string, nullable | XAF criteria language; null = always enabled |
| ConfirmationMessage | string, nullable | Null = no confirmation dialog |
| IsActive | bool, default true | Inactive actions are ignored by the dispatcher |
| Steps | IList<CustomActionStep> | `[Aggregated]`, cascade delete (both XAF attribute and Fluent API `OnDelete(Cascade)`, per CustomField precedent) |

`Module/BusinessObjects/CustomActionStep.cs` — derives `BaseObject`:

| Property | Type | Notes |
|---|---|---|
| CustomActionId | Guid? explicit FK | Same pattern as CustomField.CustomClassId |
| SortOrder | int | Execution order |
| Kind | enum StepKind { SetField, ShowMessage, OpenView } | Stored as string via `.HasConversion<string>()` (Status precedent) |
| FieldName | string | SetField only |
| Value | string | SetField only; literal, converted to member type at execution |
| MessageText | string | ShowMessage only |
| MessageType | enum StepMessageType { Info, Success, Warning, Error }, default Info | ShowMessage only; stored as string |
| TargetEntityName | string | OpenView only |

DbContext: two `DbSet`s + OnModelCreating config (enum conversions, FK, cascade). Tables are
created by XAF's standard database update (auto-update on version mismatch is already always-on
in `BlazorApplication.DatabaseVersionMismatch`).

UI: CustomAction ListView + DetailView with Steps as nested grid (aggregated collection),
both under the Schema Management nav group. No custom editors in v1.

## Dispatcher

`Module/Controllers/MetadataActionDispatcherController.cs`, `ViewController<DetailView>`
(no object-type constraint → considered for every DetailView).

**Implementation amendment (2026-07-19, verified against DX 26.1 sources during Task 2):**
XAF Blazor resolves toolbar/Ribbon container membership from the Application Model, which is
generated from CONSTRUCTOR-declared controller actions before any view activates — actions
created dynamically in `OnActivated` never render (and `Controller.Actions` has no `Remove`,
so per-activation re-adds would throw on duplicate ids). The dispatcher therefore declares a
fixed POOL of 10 slot `SimpleAction`s in its constructor (`CustomActionSlot0..9`,
PredefinedCategory.Edit) and, per activation, assigns matching metadata rows to slots
(Caption/ConfirmationMessage/criteria) and hides unused slots via `Active[key] = false`.
**Ceiling: 10 active actions per target entity type**; overflow is logged and slot
assignment is deterministic (ordered by Caption, then ID).

OnActivated:
1. Open a dedicated `IObjectSpace` for `CustomAction` (never the view's — keeps the view's
   object space unmodified). Dispose it on deactivation.
2. Query: `IsActive && TargetEntity == View.ObjectTypeInfo.Name`. Fresh query per
   activation = live behavior; no caching in v1.
3. Assign each row (ordered by Caption, then ID) to the next pool slot: set Caption and
   ConfirmationMessage from metadata; `Execute` handlers are subscribed once in the
   constructor and route to the assigned row via a per-activation slot→row map.
4. Parse Criteria once (`CriteriaOperator.Parse` in try/catch). Unparseable → action
   disabled via BoolList reason `"InvalidCriteria"` + `ILogger` warning; never a user-facing
   throw.
5. Subscribe `View.CurrentObjectChanged` and `View.ObjectSpace.ObjectChanged` to re-evaluate
   enablement: `View.ObjectSpace.IsObjectFitForCriteria(criteria, View.CurrentObject)` →
   `action.Enabled["CriteriaFit"]`. Null criteria = always enabled.

OnDeactivated: unsubscribe all handlers (reverse order). Slot actions are NOT removed — they
persist for the controller's lifetime with `Execute` subscribed once, in the constructor.
Deactivation instead clears the slot→row map (so a stale click after deactivation misses the
lookup and shows a friendly "no longer available" message instead of touching a disposed
object space), resets each slot's `Active`/`Enabled` BoolList keys, and disposes the metadata
object space. Follows the xaf-viewcontroller-patterns rules (no ObjectSpace leaks, no dangling
event subscriptions).

## Step Execution

On `Execute` (runs in the SimpleAction handler, `SimpleActionExecuteEventArgs e`):

1. Load the action's steps ordered by SortOrder (from the metadata object space).
2. Iterate in order:
   - **SetField**: resolve `View.ObjectTypeInfo.FindMember(FieldName)`; null → abort (see
     errors). Convert `Value` to the member type: numerics/bool/DateTime/Guid via invariant
     parse, string passthrough, empty string → null for nullable members; failure → abort.
     `member.SetValue(View.CurrentObject, converted)` (in-memory only).
   - **ShowMessage**: `Application.ShowViewStrategy.ShowMessage(text, InformationType per
     MessageType)` — displays immediately, in step order.
   - **OpenView**: record the target; after the loop, set `e.ShowViewParameters.CreatedView`
     to `Application.CreateListView(targetType)`. Target type resolution tries, in order:
     runtime types (`XafDynamicAssembliesEFCoreDbContext.RuntimeEntityTypes`) by simple name,
     then compiled `XafTypesInfo.Instance.PersistentTypes` by simple name, then
     `XafTypesInfo.Instance.FindTypeInfo` as a fully-qualified-name fallback (existing
     behavior). Metadata validation guarantees at most one OpenView step.
3. If at least one SetField executed and no step aborted: one `View.ObjectSpace.CommitChanges()`.
   The view updates immediately (its own object space committed).

Error handling:
- Unknown field / conversion failure: show an Error message naming the step, skip all
  remaining steps, skip the commit. In-memory changes from already-executed SetField steps
  of this click remain uncommitted — visible and cancelable by the user. Documented behavior.
- Commit-time validation failure: XAF's standard validation dialog surfaces; nothing extra.
- Dispatcher never crashes the view: all metadata access wrapped; failures log + disable.

## Metadata Validation (XAF Validation module, on save)

- RuleRequiredField: Caption, TargetEntity; kind-conditional requireds via criteria-based
  rules (SetField→FieldName, ShowMessage→MessageText, OpenView→TargetEntityName)
- RuleCombinationOfPropertiesIsUnique: (TargetEntity, Caption)
- At most one OpenView step per action (code rule on CustomAction save)
- Criteria parseability checked on save as a **warning** (target type may not exist yet)

## Testing

New `XafDynamicAssemblies.Tests/Tests/Phase12_ActionBuilderTests.cs`, existing suite pattern
(`[Collection("Sequential")]`, IAsyncLifetime page lifecycle, `Test_NN_` naming). ~8–10 tests:

1. Seed a runtime probe entity (DB helpers, existing idiom) with a Status-like field; deploy once.
2. Create a CustomAction via UI (Caption "Approve", SetField Status=Approved + ShowMessage).
3. Open the probe entity DetailView → button present **without server restart**.
4. Click → field committed (assert via DB), message shown.
5. Criteria case: action disabled when criteria unmet, enabled when met.
6. OpenView case: action opens the named entity's ListView.
7. Negative: step validation blocks saving a SetField step without FieldName.
8. Inactive action does not render.
9. Cleanup: delete action + probe entity metadata, drop table, redeploy.

The phase itself needs only ONE deploy (probe entity seeding) — actions are live, so the
suite stays fast. Full regression (`Category!=LiveAI`) is the merge gate, as established.

## File Inventory (new/modified)

- New: `Module/BusinessObjects/CustomAction.cs`, `CustomActionStep.cs`
- New: `Module/Controllers/MetadataActionDispatcherController.cs`
- Modified: `Module/BusinessObjects/XafDynamicAssembliesDbContext.cs` (DbSets + config)
- New: `XafDynamicAssemblies.Tests/Tests/Phase12_ActionBuilderTests.cs`
- Modified: docs (README features, CLAUDE.md architecture table, TODO/DONE on completion)
