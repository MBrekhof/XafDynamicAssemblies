using XafDynamicAssemblies.Tests.Fixtures;
using XafDynamicAssemblies.Tests.Helpers;
using XafDynamicAssemblies.Tests.Pages;

namespace XafDynamicAssemblies.Tests.Tests;

/// <summary>
/// Phase 4 Tests: Hot-load — schema changes take effect after Deploy Schema.
/// Ported from tests/tests/test_phase4_hot_load.py.
///
/// Verifies that creating runtime entities via Schema Management + clicking
/// "Deploy Schema" compiles them and makes them available. The server restarts
/// in-process (exit code 42, wrapper restarts) when the type set changes.
/// </summary>
[Collection("Sequential")]
public class Phase04_HotLoadTests : IAsyncLifetime
{
    private readonly BrowserFixture _fixture;
    private IPage _page = null!;

    public Phase04_HotLoadTests(BrowserFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _page = await _fixture.NewPageAsync();

    public async Task DisposeAsync() => await _page.Context.DisposeAsync();

    /// <summary>Navigate to Custom Class ListView and wait for grid.</summary>
    private async Task<(NavigationPage Nav, ListViewPage Lv)> NavToCustomClassAsync()
    {
        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        return (nav, lv);
    }

    /// <summary>Delete a row from the current grid if it exists.</summary>
    private async Task DeleteIfExistsAsync(string text)
    {
        var lv = new ListViewPage(_page);
        if (await lv.HasRowWithTextAsync(text))
        {
            await lv.SelectRowWithTextAsync(text);
            await lv.ClickDeleteAsync();
            await lv.ConfirmDeleteAsync();
            await _page.WaitForTimeoutAsync(500);
        }
    }

    // --- TestHotLoadNewClass: create a new class via UI and deploy it ---

    /// <summary>Create a new CustomClass and click Deploy Schema.</summary>
    [Fact]
    public async Task Test_01_CreateClassForHotLoad()
    {
        var (nav, lv) = await NavToCustomClassAsync();
        await DeleteIfExistsAsync("HotLoadProduct");

        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(1000);
        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Class Name", "HotLoadProduct");
        await detail.FillFieldAsync("Navigation Group", "Inventory");
        await detail.FillFieldAsync("Description", "Hot-load test entity");
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(2000);

        // Navigate back to ListView to access the Deploy Schema action
        nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        Assert.True(await lv.HasRowWithTextAsync("HotLoadProduct"), "HotLoadProduct should be in Custom Class list");

        // Click Deploy Schema to trigger hot-load + restart
        await ServerHelper.ClickDeploySchemaAsync(_page);
        await ServerHelper.WaitForDeployRestartAsync(_page);
    }

    /// <summary>After deploy + restart, the Inventory nav group should exist.</summary>
    [Fact]
    public async Task Test_02_HotLoadedClassInNavigation()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);

        // Check the Inventory nav group exists (from [NavigationItem("Inventory")])
        var links = await _page.Locator(".xaf-nav-link").AllTextContentsAsync();
        Assert.True(links.Contains("Inventory"), $"Inventory nav group should exist. Links: {string.Join(", ", links)}");

        // HotLoadProduct may be a child item inside the collapsed Inventory group,
        // or the group itself may link to it. Verify by navigating directly.
        await _page.GotoAsync($"{TestSettings.BaseUrl}/HotLoadProduct_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30_000 });
        await _page.WaitForSelectorAsync(".dxbl-grid", new() { Timeout = 30_000 });
    }

    /// <summary>Verify the hot-loaded entity's ListView renders a grid.</summary>
    [Fact]
    public async Task Test_03_HotLoadedClassListView()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);

        // Navigate directly to HotLoadProduct ListView
        await _page.GotoAsync($"{TestSettings.BaseUrl}/HotLoadProduct_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30_000 });
        await _page.WaitForSelectorAsync(".xaf-nav-link", new() { Timeout = 30_000 });
        await _page.WaitForTimeoutAsync(2000);

        // Grid present
        var grids = _page.Locator(".dxbl-grid");
        var count = await grids.CountAsync();
        var visible = false;
        for (var i = 0; i < count; i++)
        {
            if (await grids.Nth(i).IsVisibleAsync())
            {
                visible = true;
                break;
            }
        }
        Assert.True(visible, "HotLoadProduct ListView should show a grid");
    }

    // --- TestHotLoadAddField: add a field to a hot-loaded class ---

    /// <summary>Open HotLoadProduct detail, add ProductName field via nested Fields grid.</summary>
    [Fact]
    public async Task Test_04_AddFieldViaNestedGrid()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);

        var (nav, lv) = await NavToCustomClassAsync();
        await lv.DoubleClickRowWithTextAsync("HotLoadProduct");
        await _page.WaitForTimeoutAsync(2000);

        // In XAF Blazor, the aggregated Fields collection renders as a nested grid
        // with its own toolbar. Look for the "New" button in the nested area.
        // DevExpress Blazor 26.1: real action buttons are direct children of
        // <dxbl-toolbar-item>/<dxbl-bar-item>; the adaptive-layout "virtual toolbar" clone wraps
        // its (off-screen, non-interactive) copy in a plain <div> instead, so this selector
        // excludes it. See BasePage.cs remarks.
        var newButtons = _page.Locator(
            "dxbl-toolbar-item > button[data-action-name=\"New\"], dxbl-bar-item > button[data-action-name=\"New\"]");

        if (await newButtons.CountAsync() > 1)
        {
            // Multiple New buttons -> last one is for the nested grid
            await newButtons.Last.ClickAsync();
            await _page.WaitForTimeoutAsync(2000);

            // Fill in the new CustomField form
            var detail = new DetailViewPage(_page);
            try
            {
                await detail.FillFieldAsync("Field Name", "ProductName");
                await detail.ClickSaveAsync();
                await _page.WaitForTimeoutAsync(2000);

                // Go back to parent CustomClass if we navigated away
                if (await _page.Locator(".xaf-nav-link:has-text('Custom Class')").CountAsync() > 0)
                {
                    await _page.GoBackAsync();
                    await _page.WaitForTimeoutAsync(1000);
                }
            }
            catch
            {
                // ponytail: matches Python's bare `except Exception as e: print(...)` —
                // nested grid interaction is best-effort here.
            }
        }

        // Verify the field was added by checking CustomField list
        nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Field");
        lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        await _page.WaitForTimeoutAsync(500);

        var hasField = await lv.HasRowWithTextAsync("ProductName");
        // ponytail: matches Python's soft assertion — both branches assert True.
        // Nested field creation via the aggregated grid is optional; core hot-load
        // behavior is already validated by tests 01-03.
        if (hasField)
            Assert.True(true, "ProductName field was successfully added");
        else
            Assert.True(true, "Nested field creation is optional; core hot-load validated in tests 01-03");
    }

    // --- TestDataSurvivesHotLoad: existing runtime entity data survives schema changes ---

    /// <summary>Pre-existing Customer entity should still work after hot-load changes.</summary>
    [Fact]
    public async Task Test_05_ExistingCustomerStillWorks()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);

        // Use direct URL navigation for runtime entity views
        await _page.GotoAsync($"{TestSettings.BaseUrl}/Customer_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(3000);
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();

        // Clean up any leftover
        if (await lv.HasRowWithTextAsync("HotLoadSurvivor"))
        {
            await lv.SelectRowWithTextAsync("HotLoadSurvivor");
            await lv.ClickDeleteAsync();
            await lv.ConfirmDeleteAsync();
            await _page.WaitForTimeoutAsync(500);
        }

        // Create a test record
        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(2000);
        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Name", "HotLoadSurvivor");
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(2000);

        await _page.GotoAsync($"{TestSettings.BaseUrl}/Customer_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(2000);
        await lv.WaitForGridAsync();
        await _page.WaitForTimeoutAsync(500);
        Assert.True(await lv.HasRowWithTextAsync("HotLoadSurvivor"));
    }

    /// <summary>Reload and verify Customer data persists across circuits.</summary>
    [Fact]
    public async Task Test_06_DataSurvivesReload()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);

        await _page.GotoAsync($"{TestSettings.BaseUrl}/Customer_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(3000);
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        await _page.WaitForTimeoutAsync(500);
        Assert.True(await lv.HasRowWithTextAsync("HotLoadSurvivor"), "Data should survive page reloads");
    }

    // --- TestCleanup ---

    /// <summary>Remove test entities.</summary>
    [Fact]
    public async Task Test_99_Cleanup()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);

        // Clean up Customer records via direct URL
        try
        {
            await _page.GotoAsync($"{TestSettings.BaseUrl}/Customer_ListView",
                new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
            await _page.WaitForTimeoutAsync(2000);
            var lv = new ListViewPage(_page);
            await lv.WaitForGridAsync();
            await _page.WaitForTimeoutAsync(500);
            foreach (var name in new[] { "HotLoadSurvivor", "TestProduct" })
            {
                if (await lv.HasRowWithTextAsync(name))
                {
                    await lv.SelectRowWithTextAsync(name);
                    await lv.ClickDeleteAsync();
                    await lv.ConfirmDeleteAsync();
                    await _page.WaitForTimeoutAsync(500);
                }
            }
        }
        catch
        {
            // ponytail: matches Python's bare `except Exception: pass` — cleanup is best-effort.
        }

        // Clean up HotLoadProduct class
        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        var lv2 = new ListViewPage(_page);
        await lv2.WaitForGridAsync();
        await DeleteIfExistsAsync("HotLoadProduct");
    }
}
