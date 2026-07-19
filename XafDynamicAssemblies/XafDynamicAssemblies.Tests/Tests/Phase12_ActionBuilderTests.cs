using Npgsql;
using XafDynamicAssemblies.Tests.Fixtures;
using XafDynamicAssemblies.Tests.Helpers;
using XafDynamicAssemblies.Tests.Pages;

namespace XafDynamicAssemblies.Tests.Tests;

/// <summary>
/// Phase 12 Tests: Metadata-driven Action Builder (ACT-001).
///
/// Verifies the CustomAction/CustomActionStep UI (Tasks 1-2) end to end: a runtime probe
/// entity is seeded once (the phase's ONLY deploy+restart), then a CustomAction with
/// SetField/ShowMessage/OpenView steps is created via the UI and exercised live -- per the
/// approved design (docs/superpowers/specs/2026-07-19-action-builder-design.md), a new or
/// changed CustomAction appears the next time its target DetailView opens, with NO deploy
/// and NO server restart.
/// </summary>
[Collection("Sequential")]
public class Phase12_ActionBuilderTests : IAsyncLifetime
{
    private readonly BrowserFixture _fixture;
    private IPage _page = null!;

    private const string ProbeClass = "ActionProbe";
    private const string ProbeNavGroup = "ActionProbeGroup";
    private const string ActionCaption = "Approve Probe";
    private const string Probe1Title = "Probe1";

    public Phase12_ActionBuilderTests(BrowserFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _page = await _fixture.NewPageAsync();

    public async Task DisposeAsync() => await _page.Context.DisposeAsync();

    // --- Navigation helpers (existing suite idiom) ---

    private async Task<(NavigationPage Nav, ListViewPage Lv)> NavToCustomClassAsync()
    {
        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        return (nav, lv);
    }

    private async Task<(NavigationPage Nav, ListViewPage Lv)> NavToCustomActionAsync()
    {
        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Action");
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        return (nav, lv);
    }

    private async Task<ListViewPage> GotoProbeListViewAsync()
    {
        await _page.GotoAsync($"{TestSettings.BaseUrl}/{ProbeClass}_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(2000);
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        return lv;
    }

    /// <summary>
    /// Create a CustomClass directly via DB (bypassing UI validation), matching Phase07's
    /// create_class_via_db() precedent. One-off helper — used only by Test_01 — kept inline
    /// per the Phase06/Phase07 precedent rather than moving into DatabaseHelper.
    /// </summary>
    private static void CreateClassViaDb(string className, string navGroup, string description = "")
    {
        using var conn = DatabaseHelper.GetConnection();
        using (var deleteCmd = new NpgsqlCommand(
            "DELETE FROM \"CustomClasses\" WHERE \"ClassName\" = @name", conn))
        {
            deleteCmd.Parameters.AddWithValue("name", className);
            deleteCmd.ExecuteNonQuery();
        }
        using var insertCmd = new NpgsqlCommand(@"
            INSERT INTO ""CustomClasses"" (""ID"", ""ClassName"", ""NavigationGroup"", ""Description"",
                ""Status"", ""GCRecord"", ""OptimisticLockField"")
            VALUES (gen_random_uuid(), @name, @navGroup, @description, 'Runtime', 0, 0)", conn);
        insertCmd.Parameters.AddWithValue("name", className);
        insertCmd.Parameters.AddWithValue("navGroup", navGroup);
        insertCmd.Parameters.AddWithValue("description", description);
        insertCmd.ExecuteNonQuery();
    }

    // --- Action-builder UI helpers ---
    //
    // DOM facts verified live against the running app (DX 26.1 Blazor) before writing these
    // tests -- see task-3-report.md for the full exploration log:
    //  - The Steps nested grid's own "New" button shares data-action-name="New" with the
    //    parent ribbon's "New"; it is always the LAST match (Phase04 nested-grid precedent).
    //  - Opening the nested grid's "New" auto-commits the parent CustomAction's currently
    //    filled fields (Caption/Target Entity) to the DB immediately -- no explicit parent
    //    Save is required, and parent Caption/Target Entity MUST be filled before the nested
    //    "New" is clicked or it blocks with inline "must not be empty" errors instead of
    //    opening the step sub-view.
    //  - Each Action Step opens as its own view TAB. Tab content for an INACTIVE tab is NOT
    //    removed from the DOM (only display:none is avoided -- it's parked off-screen via
    //    `visibility:hidden` + negative positioning), so leaving two step tabs open at once
    //    creates two DOM nodes per data-item-name field. To keep field lookups (which use
    //    `.First`) unambiguous, every step tab is closed immediately after it is saved
    //    (verified: closing the active step tab returns focus to the parent tab; closing an
    //    unsaved/validation-blocked tab silently discards it with no confirmation dialog).
    //  - The Kind combobox's popup options render with a real `role="option"` attribute and
    //    the exact humanized enum text ("Set field" / "Show message" / "Open view").

    private async Task ClickNestedNewAsync()
    {
        var newButtons = _page.Locator(
            "dxbl-toolbar-item > button[data-action-name=\"New\"], dxbl-bar-item > button[data-action-name=\"New\"]");
        await newButtons.Last.ClickAsync();
        await _page.WaitForTimeoutAsync(1500);
    }

    private async Task SelectComboOptionAsync(string fieldLabel, string optionText)
    {
        await _page.Locator($".dxbl-fl-ctrl:has([data-item-name='{fieldLabel}']) button").ClickAsync();
        await _page.WaitForTimeoutAsync(400);
        await _page.Locator($"[role='option']:has-text(\"{optionText}\")").First.ClickAsync();
        await _page.WaitForTimeoutAsync(400);
    }

    private async Task IncrementSortOrderAsync(int times)
    {
        var incBtn = _page.Locator(".dxbl-fl-ctrl:has([data-item-name='Sort Order']) button").First;
        for (var i = 0; i < times; i++)
        {
            await incBtn.ClickAsync();
            await _page.WaitForTimeoutAsync(200);
        }
    }

    /// <summary>Close the currently active view tab (works for both saved and discarded/unsaved tabs).</summary>
    private async Task CloseActiveTabAsync()
    {
        // The close ("x") button is CSS `visibility: hidden` at rest and is only meant to be
        // revealed on tab hover -- but verified live that even a genuine Playwright
        // .HoverAsync() (confirmed applied: `tab.matches(':hover')` was true afterward) does
        // NOT flip its computed visibility, so Playwright's .ClickAsync() actionability check
        // times out waiting for "visible" no matter what. A native DOM .click() bypasses that
        // gate and Blazor still processes it correctly for this element -- same technique,
        // same justification as NavigationPage.ExpandGroupJsAsync uses for the nav pane's
        // CSS-hover-gated expand button.
        await _page.EvaluateAsync(@"() => {
            const tabs = Array.from(document.querySelectorAll(""[role='tab'][aria-selected='true']""));
            const tab = tabs[tabs.length - 1];
            const buttons = tab.querySelectorAll('button');
            const closeBtn = buttons[buttons.length - 1];
            closeBtn.click();
        }");
        await _page.WaitForTimeoutAsync(800);
    }

    /// <summary>
    /// Add one CustomActionStep via the nested Steps grid: open, set Kind/SortOrder, fill the
    /// kind-specific field(s), save, then close the step's tab (see class-level DOM notes).
    /// </summary>
    private async Task AddStepAsync(int sortOrder, string kind, string? fieldName = null,
        string? value = null, string? messageText = null, string? targetEntityName = null)
    {
        await ClickNestedNewAsync();

        if (kind != "Set field")
            await SelectComboOptionAsync("Kind", kind);
        if (sortOrder > 0)
            await IncrementSortOrderAsync(sortOrder);

        var detail = new DetailViewPage(_page);
        if (fieldName != null) await detail.FillFieldAsync("Field Name", fieldName);
        if (value != null) await detail.FillFieldAsync("Value", value);
        if (messageText != null) await detail.FillFieldAsync("Message Text", messageText);
        if (targetEntityName != null) await detail.FillFieldAsync("Target Entity Name", targetEntityName);

        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(1000);
        await CloseActiveTabAsync();
    }

    /// <summary>
    /// Locate the metadata-driven action button by its rendered Caption (data-action-name),
    /// mirroring BasePage.ClickActionAsync's 26.1 selector (private there, so re-declared here
    /// per the brief's exact selector) with a text fallback.
    /// </summary>
    private async Task<ILocator> ActionButtonAsync(string caption)
    {
        var loc = _page.Locator(
            $"dxbl-toolbar-item > button[data-action-name=\"{caption}\"], dxbl-bar-item > button[data-action-name=\"{caption}\"]");
        if (await loc.CountAsync() == 0)
            loc = _page.Locator($"button:has-text(\"{caption}\")");
        return loc;
    }

    /// <summary>
    /// Adapted from Phase03's TrySaveAndCheckValidationAsync: click Save on the currently open
    /// Action Step form and report whether XAF's validation blocked it. Phase03's own keyword
    /// list ("must be", "cannot be", ...) does NOT match this rule's actual message ("must
    /// NOT be empty" -- verified live), so this checks for the `[role='alert']` toast XAF
    /// Blazor renders on a validation failure instead of keyword-matching body text.
    /// </summary>
    private async Task<(bool Saved, string ErrorText)> TrySaveStepAndCheckValidationAsync()
    {
        await _page.Locator(
            "dxbl-toolbar-item > button[data-action-name=\"Save\"], dxbl-bar-item > button[data-action-name=\"Save\"]"
        ).First.ClickAsync();
        await _page.WaitForTimeoutAsync(1500);

        var alert = _page.Locator("[role='alert']");
        if (await alert.CountAsync() > 0)
            return (false, await alert.First.InnerTextAsync());
        return (true, "");
    }

    // --- Tests ---

    /// <summary>Seed the ActionProbe runtime entity and deploy — the ONLY deploy in this phase.</summary>
    [Fact]
    public async Task Test_01_SeedProbeEntity()
    {
        CreateClassViaDb(ProbeClass, ProbeNavGroup, "Probe entity for ACT-001 Phase 12 tests");
        DatabaseHelper.InsertFieldViaDb(ProbeClass, "Title", "System.String", isDefault: true);
        DatabaseHelper.InsertFieldViaDb(ProbeClass, "Status", "System.String");
        DatabaseHelper.InsertFieldViaDb(ProbeClass, "Amount", "System.Int32");

        var (_, lv) = await NavToCustomClassAsync();
        Assert.True(await lv.HasRowWithTextAsync(ProbeClass), "ActionProbe class should exist before deploy");

        await ServerHelper.ClickDeploySchemaAsync(_page);
        await ServerHelper.WaitForDeployRestartAsync(_page);

        var links = await _page.Locator(".xaf-nav-link").AllTextContentsAsync();
        Assert.Contains(ProbeNavGroup, links);
    }

    /// <summary>Create the CustomAction via the UI: SetField Status=Approved + ShowMessage. No deploy.</summary>
    [Fact]
    public async Task Test_02_CreateActionViaUi()
    {
        var (nav, lv) = await NavToCustomActionAsync();
        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(1500);

        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Caption", ActionCaption);
        await detail.FillFieldAsync("Target Entity", ProbeClass);

        await AddStepAsync(sortOrder: 0, kind: "Set field", fieldName: "Status", value: "Approved");
        await AddStepAsync(sortOrder: 1, kind: "Show message", messageText: "Probe approved!");

        await nav.NavigateToAsync("Schema Management", "Custom Action");
        await lv.WaitForGridAsync();
        Assert.True(await lv.HasRowWithTextAsync(ActionCaption), "Approve Probe action should exist");
    }

    /// <summary>
    /// The button must render the next time the ActionProbe DetailView opens — with NO deploy
    /// and NO server restart. This test (and everything since Test_01's seed deploy) never
    /// calls ServerHelper.ClickDeploySchemaAsync/WaitForDeployRestartAsync — that omission
    /// itself is the "no restart" proof, per the ACT-001 design's live-activation guarantee.
    /// </summary>
    [Fact]
    public async Task Test_03_ButtonAppearsWithoutRestart()
    {
        var lv = await GotoProbeListViewAsync();
        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(1500);
        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Title", Probe1Title);
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(1500);

        // Explicitly reopen from the ListView (brief: "open its DetailView").
        lv = await GotoProbeListViewAsync();
        await lv.DoubleClickRowWithTextAsync(Probe1Title);
        await _page.WaitForTimeoutAsync(1500);

        var btn = await ActionButtonAsync(ActionCaption);
        Assert.True(await btn.CountAsync() > 0, "Approve Probe button should render without a restart");
    }

    /// <summary>Click the action: DB Status is set and the ShowMessage toast appears.</summary>
    [Fact]
    public async Task Test_04_ExecuteSetsFieldAndShowsMessage()
    {
        var lv = await GotoProbeListViewAsync();
        await lv.DoubleClickRowWithTextAsync(Probe1Title);
        await _page.WaitForTimeoutAsync(1500);

        var btn = await ActionButtonAsync(ActionCaption);
        await btn.First.ClickAsync();
        await _page.WaitForTimeoutAsync(1500);

        // ShowMessage toast: XAF Blazor renders it as [role='alert'] > ... > .xaf-alert-message
        // (verified live; the plain body-text scan used by earlier phases also works, but this
        // is the precise, load-bearing element).
        var alertText = _page.Locator("[role='alert'] .xaf-alert-message");
        Assert.True(await alertText.CountAsync() > 0, "ShowMessage toast should appear");
        Assert.Contains("Probe approved!", await alertText.First.InnerTextAsync());

        using var conn = DatabaseHelper.GetConnection();
        using var cmd = new NpgsqlCommand($"SELECT \"Status\" FROM \"{ProbeClass}\" WHERE \"Title\" = @t", conn);
        cmd.Parameters.AddWithValue("t", Probe1Title);
        var status = (string?)cmd.ExecuteScalar();
        Assert.Equal("Approved", status);
    }

    /// <summary>Criteria "Status != 'Approved'" disables the button on the now-approved record, enables on a fresh one.</summary>
    [Fact]
    public async Task Test_05_CriteriaDisablesAction()
    {
        var (_, actionLv) = await NavToCustomActionAsync();
        await actionLv.DoubleClickRowWithTextAsync(ActionCaption);
        await _page.WaitForTimeoutAsync(1500);
        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Criteria", "Status != 'Approved'");
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(1500);

        // Reopen the already-approved Probe1 -> disabled.
        var lv = await GotoProbeListViewAsync();
        await lv.DoubleClickRowWithTextAsync(Probe1Title);
        await _page.WaitForTimeoutAsync(1500);
        var btn = await ActionButtonAsync(ActionCaption);

        // DOM finding (inspected live, DX 26.1 Blazor): a disabled metadata action renders the
        // native HTML `disabled` attribute on <button data-action-name="..."> AND gains the
        // `dxbl-disabled` CSS class. There is no `aria-disabled` attribute on this DX version.
        // Asserting both: IsDisabledAsync() (checks the `disabled` property) is the primary,
        // load-bearing check; the class-name check corroborates it.
        Assert.True(await btn.First.IsDisabledAsync(), "Approve Probe should be disabled for an already-approved record");
        var cls = await btn.First.GetAttributeAsync("class") ?? "";
        Assert.Contains("dxbl-disabled", cls);

        // Fresh (new, unsaved -> Status empty, != 'Approved') record -> enabled.
        lv = await GotoProbeListViewAsync();
        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(1500);
        var btn2 = await ActionButtonAsync(ActionCaption);
        Assert.False(await btn2.First.IsDisabledAsync(), "Approve Probe should be enabled for a fresh unapproved record");
    }

    /// <summary>
    /// Add an OpenView step targeting CustomClass by its simple name; executing it navigates to
    /// the Custom Class ListView.
    /// </summary>
    [Fact]
    public async Task Test_06_OpenViewStep()
    {
        var (_, actionLv) = await NavToCustomActionAsync();
        await actionLv.DoubleClickRowWithTextAsync(ActionCaption);
        await _page.WaitForTimeoutAsync(1500);

        await AddStepAsync(sortOrder: 2, kind: "Open view",
            targetEntityName: "CustomClass");

        // Fresh unapproved record so the Test_05 criteria keeps the action enabled.
        var lv = await GotoProbeListViewAsync();
        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(1500);

        var btn = await ActionButtonAsync(ActionCaption);
        await btn.First.ClickAsync();
        await _page.WaitForTimeoutAsync(2000);

        Assert.Contains("CustomClass_ListView", _page.Url);
        var classLv = new ListViewPage(_page);
        await classLv.WaitForGridAsync();
        Assert.True(await classLv.HasRowWithTextAsync(ProbeClass),
            "Custom Class list should show ActionProbe (existing list marker from Phase01)");
    }

    /// <summary>Saving a SetField step with an empty Field Name is blocked by validation.</summary>
    [Fact]
    public async Task Test_07_ValidationBlocksEmptyFieldName()
    {
        var (_, actionLv) = await NavToCustomActionAsync();
        await actionLv.DoubleClickRowWithTextAsync(ActionCaption);
        await _page.WaitForTimeoutAsync(1500);

        await ClickNestedNewAsync();
        // Kind stays at its default "Set field"; Field Name is left empty on purpose ->
        // RuleRequiredField(TargetCriteria="Kind = 'SetField'") on CustomActionStep.FieldName.
        var (saved, errorText) = await TrySaveStepAndCheckValidationAsync();

        Assert.False(saved, "Save should fail for a SetField step with an empty Field Name");
        Assert.Contains("Field Name", errorText);

        // Discard the blocked, never-persisted step (verified live: no confirmation dialog).
        await CloseActiveTabAsync();
    }

    /// <summary>Setting Is Active=false hides the button on the next DetailView open.</summary>
    [Fact]
    public async Task Test_08_InactiveActionHidden()
    {
        var (_, actionLv) = await NavToCustomActionAsync();
        await actionLv.DoubleClickRowWithTextAsync(ActionCaption);
        await _page.WaitForTimeoutAsync(1500);
        var detail = new DetailViewPage(_page);
        await detail.SetCheckboxAsync("Is Active", false);
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(1500);

        var lv = await GotoProbeListViewAsync();
        await lv.DoubleClickRowWithTextAsync(Probe1Title);
        await _page.WaitForTimeoutAsync(1500);

        var btn = await ActionButtonAsync(ActionCaption);
        Assert.Equal(0, await btn.CountAsync());
    }

    /// <summary>Delete the CustomAction (UI) and probe metadata/table (DB); deploy; verify nav is clean.</summary>
    [Fact]
    public async Task Test_99_Cleanup()
    {
        var (_, actionLv) = await NavToCustomActionAsync();
        if (await actionLv.HasRowWithTextAsync(ActionCaption))
        {
            await actionLv.SelectRowWithTextAsync(ActionCaption);
            await actionLv.ClickDeleteAsync();
            await actionLv.ConfirmDeleteAsync();
            await _page.WaitForTimeoutAsync(1000);
        }
        Assert.False(await actionLv.HasRowWithTextAsync(ActionCaption), "Approve Probe action should be deleted");

        DatabaseHelper.DeleteCustomClass(ProbeClass);
        using (var conn = DatabaseHelper.GetConnection())
        using (var cmd = new NpgsqlCommand($"DROP TABLE IF EXISTS \"{ProbeClass}\" CASCADE", conn))
        {
            cmd.ExecuteNonQuery();
        }

        var (_, classLv) = await NavToCustomClassAsync();
        Assert.False(await classLv.HasRowWithTextAsync(ProbeClass), "ActionProbe metadata should be gone before deploy");
        await ServerHelper.ClickDeploySchemaAsync(_page);
        await ServerHelper.WaitForDeployRestartAsync(_page);

        var links = await _page.Locator(".xaf-nav-link").AllTextContentsAsync();
        Assert.DoesNotContain(ProbeNavGroup, links);
    }
}
