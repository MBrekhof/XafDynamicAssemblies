# ACT-001 Metadata-Driven Action Builder Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Admins define buttons (SetField/ShowMessage/OpenView steps) on entity DetailViews as pure metadata — live, no compilation, no restart.

**Architecture:** Two compiled XAF EF Core entities (`CustomAction` + aggregated `CustomActionStep`) edited via standard XAF views, plus one compiled `ViewController<DetailView>` that materializes `SimpleAction`s from metadata on every view activation and interprets the steps on execute. Spec: `docs/superpowers/specs/2026-07-19-action-builder-design.md`.

**Tech Stack:** .NET 8, DevExpress XAF 26.1.3 (Blazor), EF Core 8, PostgreSQL, xUnit + Microsoft.Playwright E2E suite.

## Global Constraints

- DevExpress rule (user-mandated): NEVER assume DX API surface — verify signatures via dxdocs MCP (`ToolSearch "select:mcp__dxdocs__devexpress_docs_search,mcp__dxdocs__devexpress_docs_get_content"`) or installed 26.1 sources at `C:\Program Files\DevExpress 26.1\Components\Sources`. The plan's code is the intended shape; where a note says "verify via dxdocs", the verified signature governs.
- XAF EF Core entity rules: ALL properties `virtual`; collections `ObservableCollection<T>` behind `IList<T>` with `[Aggregated]`; explicit FK (`Guid?` + `[ForeignKey]`); enums stored as string via `.HasConversion<string>()`; unique indexes filtered `"GCRecord" = 0`; follow the existing `CustomClass`/`CustomField` code style in the same folder.
- ViewController rules: subscribe after `base.OnActivated()`, unsubscribe before `base.OnDeactivated()`; remove BoolList items on deactivate; dispose every caller-created IObjectSpace; no async void handlers.
- Test-suite pattern (all E2E files): `[Collection("Sequential")]`, ctor `BrowserFixture`, NO `IClassFixture<BrowserFixture>`, `IAsyncLifetime` page lifecycle, `Test_NN_` names, static cross-test state. 26.1 toolbar selectors use `dxbl-toolbar-item > button[data-action-name="<CAPTION>"], dxbl-bar-item > button[data-action-name="<CAPTION>"]` — data-action-name carries the CAPTION.
- Git: stage exact files only; NEVER `git add -A`, `git add .`, `-am`.
- Server for E2E: `run-server-mock.bat` (mock mode is harmless for non-AI phases and required if Phase 11 runs later in the same session).

---

### Task 1: CustomAction + CustomActionStep entities and DbContext registration

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Module/BusinessObjects/CustomAction.cs`
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Module/BusinessObjects/CustomActionStep.cs`
- Modify: `XafDynamicAssemblies/XafDynamicAssemblies.Module/BusinessObjects/XafDynamicAssembliesDbContext.cs`

**Interfaces:**
- Produces: `CustomAction` (Caption, TargetEntity, Criteria, ConfirmationMessage, IsActive, Steps), `CustomActionStep` (CustomActionId, SortOrder, Kind: StepKind, FieldName, Value, MessageText, MessageType: StepMessageType, TargetEntityName), enums `StepKind { SetField, ShowMessage, OpenView }`, `StepMessageType { Info, Success, Warning, Error }` — all in namespace `XafDynamicAssemblies.Module.BusinessObjects` (match the namespace used by `CustomClass.cs`; verify and follow the actual file).

- [ ] **Step 1: Read the precedent files** — `CustomClass.cs`, `CustomField.cs`, and the DbContext. Mirror their attribute usage, namespace, XML docs style, and OnModelCreating layout exactly.

- [ ] **Step 2: Create CustomAction.cs**

```csharp
using System.Collections.ObjectModel;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.Validation;

namespace XafDynamicAssemblies.Module.BusinessObjects;

[DefaultClassOptions]
[NavigationItem("Schema Management")]
[DefaultProperty(nameof(Caption))]
[XafDisplayName("Custom Action")]
public class CustomAction : BaseObject
{
    [RuleRequiredField]
    [RuleCombinationOfPropertiesIsUnique("CustomAction_Caption_Target_Unique", DefaultContexts.Save,
        nameof(Caption) + ";" + nameof(TargetEntity))]
    public virtual string Caption { get; set; } = string.Empty;

    [RuleRequiredField]
    [XafDisplayName("Target Entity")]
    public virtual string TargetEntity { get; set; } = string.Empty;

    [FieldSize(FieldSizeAttribute.Unlimited)]
    public virtual string? Criteria { get; set; }

    [XafDisplayName("Confirmation Message")]
    public virtual string? ConfirmationMessage { get; set; }

    public virtual bool IsActive { get; set; } = true;

    [Aggregated]
    public virtual IList<CustomActionStep> Steps { get; set; } = new ObservableCollection<CustomActionStep>();

    // ponytail: validated in code, not a rule class — one OpenView per action keeps
    // execute semantics unambiguous (OpenView is always effectively last)
    [RuleFromBoolProperty("CustomAction_SingleOpenView", DefaultContexts.Save,
        "An action may contain at most one OpenView step",
        UsedProperties = nameof(Steps))]
    [Browsable(false)]
    public bool HasAtMostOneOpenView => Steps.Count(s => s.Kind == StepKind.OpenView) <= 1;

    // Spec: criteria parseability is a WARNING on save (target type may not exist yet).
    // Verify ResultType syntax via dxdocs (ValidationResultType.Warning on RuleFromBoolProperty).
    [RuleFromBoolProperty("CustomAction_CriteriaParseable", DefaultContexts.Save,
        "Criteria could not be parsed — the action will be disabled until fixed",
        UsedProperties = nameof(Criteria), ResultType = ValidationResultType.Warning)]
    [Browsable(false)]
    public bool CriteriaIsParseable
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Criteria)) return true;
            try { DevExpress.Data.Filtering.CriteriaOperator.Parse(Criteria); return true; }
            catch { return false; }
        }
    }
}
```

Note: `[Browsable(false)]` needs `using System.ComponentModel;`. Verify `RuleFromBoolProperty` + `RuleCombinationOfPropertiesIsUnique` attribute signatures via dxdocs before relying on them; if the combination-rule string syntax differs in 26.1, use the documented form.

- [ ] **Step 3: Create CustomActionStep.cs**

```csharp
using System.ComponentModel.DataAnnotations.Schema;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.Validation;

namespace XafDynamicAssemblies.Module.BusinessObjects;

public enum StepKind { SetField, ShowMessage, OpenView }
public enum StepMessageType { Info, Success, Warning, Error }

[DefaultProperty(nameof(DisplayText))]
[XafDisplayName("Action Step")]
public class CustomActionStep : BaseObject
{
    public virtual Guid? CustomActionId { get; set; }

    [ForeignKey(nameof(CustomActionId))]
    public virtual CustomAction? CustomAction { get; set; }

    public virtual int SortOrder { get; set; }

    [ImmediatePostData]
    public virtual StepKind Kind { get; set; } = StepKind.SetField;

    // SetField
    [RuleRequiredField(TargetCriteria = "Kind = 'SetField'")]
    [XafDisplayName("Field Name")]
    public virtual string? FieldName { get; set; }

    public virtual string? Value { get; set; }

    // ShowMessage
    [RuleRequiredField(TargetCriteria = "Kind = 'ShowMessage'")]
    [XafDisplayName("Message Text")]
    public virtual string? MessageText { get; set; }

    public virtual StepMessageType MessageType { get; set; } = StepMessageType.Info;

    // OpenView
    [RuleRequiredField(TargetCriteria = "Kind = 'OpenView'")]
    [XafDisplayName("Target Entity Name")]
    public virtual string? TargetEntityName { get; set; }

    [NotMapped]
    [System.ComponentModel.Browsable(false)]
    public string DisplayText => Kind switch
    {
        StepKind.SetField => $"{SortOrder}. Set {FieldName} = {Value}",
        StepKind.ShowMessage => $"{SortOrder}. Message: {MessageText}",
        StepKind.OpenView => $"{SortOrder}. Open {TargetEntityName}",
        _ => SortOrder.ToString()
    };
}
```

Note: `TargetCriteria` on `[RuleRequiredField]` uses the string form of the enum (stored as string). Verify the criteria syntax against how `Status` criteria are written elsewhere in this repo (Appearance rules on `CustomClass` use the same pattern).

- [ ] **Step 4: DbContext registration** — in `XafDynamicAssembliesDbContext.cs` add:

```csharp
public DbSet<CustomAction> CustomActions { get; set; }
public DbSet<CustomActionStep> CustomActionSteps { get; set; }
```

and in `OnModelCreating` (mirroring the CustomClass/CustomField block placement):

```csharp
modelBuilder.Entity<CustomAction>(b =>
{
    b.HasMany(a => a.Steps)
     .WithOne(s => s.CustomAction)
     .HasForeignKey(s => s.CustomActionId)
     .OnDelete(DeleteBehavior.Cascade);
    b.HasIndex(nameof(CustomAction.TargetEntity), nameof(CustomAction.Caption))
     .IsUnique()
     .HasFilter("\"GCRecord\" = 0");
});
modelBuilder.Entity<CustomActionStep>(b =>
{
    b.Property(s => s.Kind).HasConversion<string>();
    b.Property(s => s.MessageType).HasConversion<string>();
});
```

- [ ] **Step 5: Build**

Run: `dotnet build XafDynamicAssemblies.slnx`
Expected: 0 errors. (New analyzer warnings from `DevExpress.ExpressApp.CodeAnalysis`: fix if trivial, otherwise report them.)

- [ ] **Step 6: Database update**

Run: `dotnet run --project XafDynamicAssemblies/XafDynamicAssemblies.Blazor.Server -- --updateDatabase --forceUpdate --silent`
Then: `docker exec xaf-dynamic-postgres psql -U xafdynamic -d XafDynamicAssemblies -c "\d \"CustomActions\"" -c "\d \"CustomActionSteps\""`
Expected: both tables exist; `CustomActionSteps.Kind` is `text`; FK to `CustomActions` present.

- [ ] **Step 7: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Module/BusinessObjects/CustomAction.cs XafDynamicAssemblies/XafDynamicAssemblies.Module/BusinessObjects/CustomActionStep.cs XafDynamicAssemblies/XafDynamicAssemblies.Module/BusinessObjects/XafDynamicAssembliesDbContext.cs
git commit -m "feat: add CustomAction/CustomActionStep metadata entities (ACT-001)"
```

---

### Task 2: Step value converter (TDD) + dispatcher controller

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/StepValueConverter.cs`
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/StepValueConverterTests.cs`
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Module/Controllers/MetadataActionDispatcherController.cs`

**Interfaces:**
- Consumes: `CustomAction`/`CustomActionStep`/`StepKind`/`StepMessageType` from Task 1.
- Produces: `static class StepValueConverter { public static object? Convert(string? raw, Type targetType); }` — throws `FormatException` with a human-readable message on unconvertible input; empty/null string → null for nullable target types, `FormatException` for non-nullable value types.

- [ ] **Step 1: Write the failing converter tests** — `StepValueConverterTests.cs` (plain unit tests, no browser/collection needed; keep OUT of the `Sequential` collection):

```csharp
using XafDynamicAssemblies.Module.Services;

namespace XafDynamicAssemblies.Tests.Tests;

public class StepValueConverterTests
{
    [Theory]
    [InlineData("hello", typeof(string), "hello")]
    [InlineData("42", typeof(int), 42)]
    [InlineData("true", typeof(bool), true)]
    [InlineData("3.5", typeof(double), 3.5)]
    public void Converts_primitives(string raw, Type target, object expected)
        => Assert.Equal(expected, StepValueConverter.Convert(raw, target));

    [Fact]
    public void Converts_decimal_invariant()
        => Assert.Equal(12.34m, StepValueConverter.Convert("12.34", typeof(decimal)));

    [Fact]
    public void Empty_string_to_nullable_is_null()
        => Assert.Null(StepValueConverter.Convert("", typeof(int?)));

    [Fact]
    public void Empty_string_to_string_stays_empty()
        => Assert.Equal("", StepValueConverter.Convert("", typeof(string)));

    [Fact]
    public void Empty_string_to_nonnullable_throws()
        => Assert.Throws<FormatException>(() => StepValueConverter.Convert("", typeof(int)));

    [Fact]
    public void Garbage_to_int_throws_with_message()
    {
        var ex = Assert.Throws<FormatException>(() => StepValueConverter.Convert("abc", typeof(int)));
        Assert.Contains("abc", ex.Message);
    }

    [Fact]
    public void Converts_guid_and_datetime()
    {
        var g = Guid.NewGuid();
        Assert.Equal(g, StepValueConverter.Convert(g.ToString(), typeof(Guid)));
        Assert.Equal(new DateTime(2026, 7, 19), StepValueConverter.Convert("2026-07-19", typeof(DateTime)));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests --filter "FullyQualifiedName~StepValueConverter"`
Expected: FAIL to compile ("StepValueConverter does not exist") — that is the RED state; capture it.

- [ ] **Step 3: Implement StepValueConverter.cs**

```csharp
using System.Globalization;

namespace XafDynamicAssemblies.Module.Services;

/// <summary>Converts CustomActionStep string literals to member types (invariant culture).</summary>
public static class StepValueConverter
{
    public static object? Convert(string? raw, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType);
        var effective = underlying ?? targetType;

        if (string.IsNullOrEmpty(raw))
        {
            if (underlying != null || !effective.IsValueType) 
                return effective == typeof(string) ? raw : null;
            throw new FormatException($"Empty value cannot be converted to non-nullable {effective.Name}.");
        }

        try
        {
            if (effective == typeof(string)) return raw;
            if (effective == typeof(Guid)) return Guid.Parse(raw);
            if (effective == typeof(bool)) return bool.Parse(raw);
            if (effective == typeof(DateTime))
                return DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None);
            if (effective.IsEnum) return Enum.Parse(effective, raw, ignoreCase: true);
            return System.Convert.ChangeType(raw, effective, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is not FormatException)
        {
            throw new FormatException($"Value '{raw}' cannot be converted to {effective.Name}: {ex.Message}");
        }
        catch (FormatException)
        {
            throw new FormatException($"Value '{raw}' cannot be converted to {effective.Name}.");
        }
    }
}
```

- [ ] **Step 4: Run to verify green**

Run: `dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests --filter "FullyQualifiedName~StepValueConverter"`
Expected: all pass (9+). No server needed.

- [ ] **Step 5: Implement MetadataActionDispatcherController.cs**

```csharp
using DevExpress.Data.Filtering;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.Persistent.Base;
using Microsoft.Extensions.Logging;
using XafDynamicAssemblies.Module.BusinessObjects;
using XafDynamicAssemblies.Module.Services;

namespace XafDynamicAssemblies.Module.Controllers;

/// <summary>
/// Materializes metadata-defined CustomActions as SimpleActions on every DetailView.
/// Live: metadata is re-read on each activation — no compilation, no restart (ACT-001).
/// </summary>
public class MetadataActionDispatcherController : ViewController<DetailView>
{
    private IObjectSpace? _metadataOs;
    private readonly List<(SimpleAction Action, CustomAction Meta, CriteriaOperator? Criteria)> _created = new();
    private ILogger<MetadataActionDispatcherController>? _logger;

    protected override void OnActivated()
    {
        base.OnActivated();
        _logger = Application.ServiceProvider?.GetService(typeof(ILogger<MetadataActionDispatcherController>))
                  as ILogger<MetadataActionDispatcherController>;
        try
        {
            _metadataOs = Application.CreateObjectSpace(typeof(CustomAction));
            var typeName = View.ObjectTypeInfo.Name;
            var rows = _metadataOs.GetObjects<CustomAction>(
                CriteriaOperator.Parse("IsActive = true And TargetEntity = ?", typeName));

            foreach (var row in rows)
            {
                var action = new SimpleAction(this, $"CustomAction_{row.ID:N}", PredefinedCategory.Edit)
                {
                    Caption = row.Caption,
                    ConfirmationMessage = row.ConfirmationMessage,
                };
                CriteriaOperator? criteria = null;
                if (!string.IsNullOrWhiteSpace(row.Criteria))
                {
                    try { criteria = CriteriaOperator.Parse(row.Criteria); }
                    catch (Exception ex)
                    {
                        action.Enabled["InvalidCriteria"] = false;
                        _logger?.LogWarning(ex, "CustomAction '{Caption}' has invalid criteria: {Criteria}",
                            row.Caption, row.Criteria);
                    }
                }
                action.Execute += CustomAction_Execute;
                _created.Add((action, row, criteria));
                Actions.Add(action);
            }

            if (_created.Count > 0)
            {
                View.CurrentObjectChanged += View_StateChanged;
                View.ObjectSpace.ObjectChanged += ObjectSpace_ObjectChanged;
                UpdateEnabledState();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Metadata action dispatcher failed to activate; no custom actions shown.");
        }
    }

    protected override void OnDeactivated()
    {
        if (_created.Count > 0)
        {
            View.CurrentObjectChanged -= View_StateChanged;
            View.ObjectSpace.ObjectChanged -= ObjectSpace_ObjectChanged;
        }
        foreach (var (action, _, _) in _created)
        {
            action.Execute -= CustomAction_Execute;
            Actions.Remove(action);
            action.Dispose();
        }
        _created.Clear();
        _metadataOs?.Dispose();
        _metadataOs = null;
        base.OnDeactivated();
    }

    private void View_StateChanged(object? sender, EventArgs e) => UpdateEnabledState();
    private void ObjectSpace_ObjectChanged(object? sender, ObjectChangedEventArgs e) => UpdateEnabledState();

    private void UpdateEnabledState()
    {
        var obj = View.CurrentObject;
        foreach (var (action, _, criteria) in _created)
        {
            if (ReferenceEquals(criteria, null)) continue;
            bool fit = false;
            try { fit = obj != null && View.ObjectSpace.IsObjectFitForCriteria(criteria, obj); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Criteria evaluation failed for {Id}", action.Id); }
            action.Enabled["CriteriaFit"] = fit;
        }
    }

    private void CustomAction_Execute(object? sender, SimpleActionExecuteEventArgs e)
    {
        var entry = _created.First(c => ReferenceEquals(c.Action, sender));
        var steps = entry.Meta.Steps.OrderBy(s => s.SortOrder).ToList();
        var obj = View.CurrentObject;
        bool anySet = false;
        Type? openViewTarget = null;

        foreach (var step in steps)
        {
            switch (step.Kind)
            {
                case StepKind.SetField:
                {
                    var member = View.ObjectTypeInfo.FindMember(step.FieldName);
                    if (member == null)
                    {
                        ShowError($"Action '{entry.Meta.Caption}': field '{step.FieldName}' not found on {View.ObjectTypeInfo.Name}.");
                        return;
                    }
                    object? converted;
                    try { converted = StepValueConverter.Convert(step.Value, member.MemberType); }
                    catch (FormatException ex)
                    {
                        ShowError($"Action '{entry.Meta.Caption}': {ex.Message}");
                        return;
                    }
                    member.SetValue(obj, converted);
                    anySet = true;
                    break;
                }
                case StepKind.ShowMessage:
                    Application.ShowViewStrategy.ShowMessage(step.MessageText,
                        step.MessageType switch
                        {
                            StepMessageType.Success => InformationType.Success,
                            StepMessageType.Warning => InformationType.Warning,
                            StepMessageType.Error => InformationType.Error,
                            _ => InformationType.Info,
                        });
                    break;
                case StepKind.OpenView:
                {
                    var ti = XafTypesInfo.Instance.FindTypeInfo(step.TargetEntityName);
                    if (ti == null)
                    {
                        ShowError($"Action '{entry.Meta.Caption}': entity '{step.TargetEntityName}' not found.");
                        return;
                    }
                    openViewTarget = ti.Type;
                    break;
                }
            }
        }

        if (anySet)
            View.ObjectSpace.CommitChanges();

        if (openViewTarget != null)
        {
            var os = Application.CreateObjectSpace(openViewTarget);
            e.ShowViewParameters.CreatedView = Application.CreateListView(os, openViewTarget, true);
            e.ShowViewParameters.TargetWindow = TargetWindow.Default;
        }
    }

    private void ShowError(string message) =>
        Application.ShowViewStrategy.ShowMessage(message, InformationType.Error);
}
```

**Verify via dxdocs before finalizing (the verified signature governs):** `IObjectSpace.IsObjectFitForCriteria` parameter order; `Application.CreateListView(IObjectSpace, Type, bool)` overload existence (if absent in 26.1, use the `FindListViewId` + `CreateCollectionSource` long form from the docs); `ObjectChangedEventArgs` namespace; `SimpleAction.Dispose` on removal (follow whatever the 26.1 docs/sources say about action cleanup in controllers — GraduateController and existing controllers in this repo are precedent). The ListView created by OpenView receives its own object space — confirm from docs whether the view disposes it (`Application.CreateListView` overloads differ); if not, use the overload that ties the object space to the view.

- [ ] **Step 6: Build + boot smoke**

Run: `dotnet build XafDynamicAssemblies.slnx` → 0 errors.
Start: `cmd //c "C:\\Projects\\XafDynamicAssemblies\\run-server-mock.bat" > /dev/null 2>&1 &`; poll `curl -sk -o /dev/null -w "%{http_code}" https://localhost:5001` until 200. Open any existing DetailView-bearing page via Playwright or curl to confirm no dispatcher crash in logs (no `Metadata action dispatcher failed` lines). Leave server RUNNING.

- [ ] **Step 7: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Module/Services/StepValueConverter.cs XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/StepValueConverterTests.cs XafDynamicAssemblies/XafDynamicAssemblies.Module/Controllers/MetadataActionDispatcherController.cs
git commit -m "feat: metadata action dispatcher controller + step value converter (ACT-001)"
```

---

### Task 3: Phase 12 E2E tests

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/Phase12_ActionBuilderTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-2; existing `DatabaseHelper`, `ServerHelper`, page objects; the suite pattern from `Phase07_ErrorHandlingTests.cs` (DB seeding idioms) and `Phase01_MetadataCrudTests.cs` (UI CRUD idioms).

Test inventory (all `Test_NN_` methods in one class; static state for the probe entity):

1. `Test_01_SeedProbeEntity` — seed runtime entity `ActionProbe` (fields: `Title` System.String, `Status` System.String, `Amount` System.Int32) via DB idioms, deploy + restart (the ONLY deploy in this phase), verify nav.
2. `Test_02_CreateActionViaUi` — UI: create CustomAction Caption `Approve Probe`, TargetEntity `ActionProbe`, one SetField step (`Status` = `Approved`) + one ShowMessage step (`Probe approved!`); save; no deploy.
3. `Test_03_ButtonAppearsWithoutRestart` — create an `ActionProbe` record, open its DetailView, assert button via 26.1 selector for caption `Approve Probe`. Assert NO server restart happened (record server process start via `netstat`/log timestamps is overkill — instead assert the whole test ran without `WaitForDeployRestartAsync`; document in a comment).
4. `Test_04_ExecuteSetsFieldAndShowsMessage` — click the action; assert DB value `Status='Approved'` for the record (Npgsql query) and the message toast text appears.
5. `Test_05_CriteriaDisablesAction` — UI: set the action's Criteria to `Status != 'Approved'`; reopen the approved record's DetailView → button disabled (assert via `button[disabled]` state or aria-disabled — inspect real DOM, don't guess; note what you found); open a fresh un-approved record → enabled.
6. `Test_06_OpenViewStep` — add an OpenView step targeting `CustomClass`; execute on a fresh record; assert navigation/view shows the Custom Class list (existing list markers from Phase01).
7. `Test_07_ValidationBlocksEmptyFieldName` — UI: try saving a SetField step without FieldName → XAF validation error surfaces (reuse Phase03's validation-detection helper pattern).
8. `Test_08_InactiveActionHidden` — set `IsActive = false` via UI; reopen probe DetailView → button absent.
9. `Test_99_Cleanup` — delete the CustomAction via UI; delete probe metadata + `DROP TABLE IF EXISTS "ActionProbe" CASCADE` via DB; deploy to remove the runtime type; verify nav clean.

- [ ] **Step 1: Write the test class** following the suite pattern (collection, lifecycle, naming). Selector for the action button (26.1): `dxbl-toolbar-item > button[data-action-name="Approve Probe"], dxbl-bar-item > button[data-action-name="Approve Probe"]` with `button:has-text("Approve Probe")` fallback — mirror `BasePage.ClickActionAsync`/`GraduateButton()` precedent.
- [ ] **Step 2: Run RED-ish first pass** — tests 02-08 must fail before Tasks 1-2 are deployed if written first; in practice Tasks 1-2 are already merged, so the meaningful check is: run the phase, debug each failure against the live DOM (screenshots), fix TESTS or report product bugs — do not weaken assertions.

Run: `dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests --filter "FullyQualifiedName~Phase12"` (server running, 15-min timeout)
Expected: 9/9 green, twice consecutively.
- [ ] **Step 3: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/Phase12_ActionBuilderTests.cs
git commit -m "test: Phase 12 E2E tests for metadata action builder (ACT-001)"
```

---

### Task 4: Documentation

**Files:**
- Modify: `README.md` (features list + test-phase table + counts)
- Modify: `CLAUDE.md` (Key Implementation Classes table + File Locations + a short "Metadata Actions" subsection under Architecture)

- [ ] **Step 1: README** — add a "Metadata-driven actions" feature bullet (live, no restart; SetField/ShowMessage/OpenView; DetailView only) and add Phase 12 (9 tests) to the test table; update total counts (verify real counts via `dotnet test --list-tests` — do not trust the plan's arithmetic).
- [ ] **Step 2: CLAUDE.md** — add `MetadataActionDispatcherController` + `StepValueConverter` to the classes table; add entity/controller file paths to File Locations; one-paragraph Metadata Actions subsection (live activation, step vocabulary, validation).
- [ ] **Step 3: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "docs: document metadata-driven action builder (ACT-001)"
```

---

## Verification (controller-level, after all tasks)

Full regression: server via `run-server-mock.bat`, `dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests --filter "Category!=LiveAI"` — all green (expected total grows by Phase 12 + converter tests). Merge gate as established.
