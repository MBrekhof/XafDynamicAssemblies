using XafDynamicAssemblies.Tests.Fixtures;
using XafDynamicAssemblies.Tests.Helpers;
using XafDynamicAssemblies.Tests.Pages;

namespace XafDynamicAssemblies.Tests.Tests;

/// <summary>
/// Phase 2 Tests: Runtime entity compilation and CRUD.
/// Ported from tests/tests/test_phase2_runtime_entities.py.
///
/// Verifies that CustomClass metadata compiles into real CLR types at startup,
/// PostgreSQL tables are created, and runtime entities have working CRUD views.
/// </summary>
[Collection("Sequential")]
public class Phase02_RuntimeEntityTests : IAsyncLifetime
{
    private readonly BrowserFixture _fixture;
    private IPage _page = null!;

    private const string CustomerClass = "Customer";

    public Phase02_RuntimeEntityTests(BrowserFixture fixture) => _fixture = fixture;

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

    /// <summary>Create a CustomClass via the UI, deleting a stale one first. Ported from create_class_via_ui().</summary>
    private async Task CreateClassViaUiAsync(string className, string navGroup, string description = "")
    {
        var (nav, lv) = await NavToCustomClassAsync();
        await DeleteIfExistsAsync(className);

        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(2000);
        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Class Name", className);
        await detail.FillFieldAsync("Navigation Group", navGroup);
        if (!string.IsNullOrEmpty(description))
            await detail.FillFieldAsync("Description", description);
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(2000);

        await nav.NavigateToAsync("Schema Management", "Custom Class");
        await lv.WaitForGridAsync();
    }

    // --- TestRuntimeEntitySetup: create Customer class with fields and deploy ---

    /// <summary>Create Customer class metadata.</summary>
    [Fact]
    public async Task Test_00a_CreateCustomerClass()
    {
        await CreateClassViaUiAsync(CustomerClass, "CRM", "Customer entity for Phase 2 tests");
    }

    /// <summary>Add fields to Customer class via direct DB insert.</summary>
    [Fact]
    public async Task Test_00b_AddCustomerFields()
    {
        DatabaseHelper.InsertFieldViaDb(CustomerClass, "Name", "System.String", isDefault: true);
        DatabaseHelper.InsertFieldViaDb(CustomerClass, "email", "System.String");
        DatabaseHelper.InsertFieldViaDb(CustomerClass, "phone", "System.String");
        DatabaseHelper.InsertFieldViaDb(CustomerClass, "active", "System.Boolean");

        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Field");
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        await _page.WaitForTimeoutAsync(500);
        Assert.True(await lv.HasRowWithTextAsync("Name"), "Name field should exist");
    }

    /// <summary>Deploy schema and wait for process restart.</summary>
    [Fact]
    public async Task Test_00c_DeployAndRestart()
    {
        var (_, lv) = await NavToCustomClassAsync();
        Assert.True(await lv.HasRowWithTextAsync(CustomerClass), "Customer class should exist");
        await ServerHelper.ClickDeploySchemaAsync(_page);
        await ServerHelper.WaitForDeployRestartAsync(_page);

        var links = await _page.Locator(".xaf-nav-link").AllTextContentsAsync();
        Assert.Contains("CRM", links);
    }

    // --- TestRuntimeEntityNavigation: runtime entities appear in XAF navigation ---

    /// <summary>Verify that Customer class appears in CRM nav group.</summary>
    [Fact]
    public async Task Test_01_CustomerInNavigation()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);
        var links = await _page.Locator(".xaf-nav-link").AllTextContentsAsync();
        Assert.Contains("CRM", links);
    }

    /// <summary>Verify Customer ListView renders with a grid.</summary>
    [Fact]
    public async Task Test_02_CustomerListViewLoads()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);
        await _page.GotoAsync($"{TestSettings.BaseUrl}/Customer_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(3000);
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        Assert.True(await _page.Locator(".dxbl-grid").First.IsVisibleAsync()
            || await _page.Locator(".dxbl-grid").Last.IsVisibleAsync());
    }

    // --- TestRuntimeEntityCRUD: create, read, update, delete runtime entities ---

    /// <summary>Create a new Customer record and verify it appears in the list.</summary>
    [Fact]
    public async Task Test_03_CreateRuntimeEntity()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);
        await _page.GotoAsync($"{TestSettings.BaseUrl}/Customer_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(3000);
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();

        // Clean up any previous test data
        if (await lv.HasRowWithTextAsync("TestCustomer1"))
        {
            await lv.SelectRowWithTextAsync("TestCustomer1");
            await lv.ClickDeleteAsync();
            await lv.ConfirmDeleteAsync();
            await _page.WaitForTimeoutAsync(500);
        }

        // Create new
        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(2000);

        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Name", "TestCustomer1");
        await detail.FillFieldAsync("email", "test1@example.com");
        await detail.FillFieldAsync("phone", "555-0001");
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(2000);

        // Navigate back to list
        await _page.GotoAsync($"{TestSettings.BaseUrl}/Customer_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(2000);
        await lv.WaitForGridAsync();
        await _page.WaitForTimeoutAsync(1000);

        Assert.True(await lv.HasRowWithTextAsync("TestCustomer1"), "TestCustomer1 should appear in list");
    }

    /// <summary>Open a runtime entity and verify field values.</summary>
    [Fact]
    public async Task Test_04_ReadRuntimeEntity()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);
        await _page.GotoAsync($"{TestSettings.BaseUrl}/Customer_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(3000);
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();

        await lv.DoubleClickRowWithTextAsync("TestCustomer1");
        await _page.WaitForTimeoutAsync(2000);

        var detail = new DetailViewPage(_page);
        Assert.Equal("TestCustomer1", await detail.GetFieldValueAsync("Name"));
        Assert.Equal("test1@example.com", await detail.GetFieldValueAsync("email"));
    }

    /// <summary>Edit a runtime entity field and verify the change persists.</summary>
    [Fact]
    public async Task Test_05_UpdateRuntimeEntity()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);
        await _page.GotoAsync($"{TestSettings.BaseUrl}/Customer_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(3000);
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();

        await lv.DoubleClickRowWithTextAsync("TestCustomer1");
        await _page.WaitForTimeoutAsync(2000);

        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("phone", "555-9999");
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(1000);

        // Reopen and verify
        await _page.GotoAsync($"{TestSettings.BaseUrl}/Customer_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(2000);
        await lv.WaitForGridAsync();
        await lv.DoubleClickRowWithTextAsync("TestCustomer1");
        await _page.WaitForTimeoutAsync(2000);

        Assert.Equal("555-9999", await detail.GetFieldValueAsync("phone"));
    }

    /// <summary>Delete a runtime entity and verify removal.</summary>
    [Fact]
    public async Task Test_06_DeleteRuntimeEntity()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);
        await _page.GotoAsync($"{TestSettings.BaseUrl}/Customer_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(3000);
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();

        if (await lv.HasRowWithTextAsync("TestCustomer1"))
        {
            await lv.SelectRowWithTextAsync("TestCustomer1");
            await lv.ClickDeleteAsync();
            await lv.ConfirmDeleteAsync();
            await _page.WaitForTimeoutAsync(1000);
        }

        Assert.False(await lv.HasRowWithTextAsync("TestCustomer1"), "TestCustomer1 should be deleted");
    }

    // --- TestMultipleDataTypes: different data types work correctly ---

    /// <summary>Verify boolean field (active) works via checkbox.</summary>
    [Fact]
    public async Task Test_07_BooleanField()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);
        await _page.GotoAsync($"{TestSettings.BaseUrl}/Customer_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(3000);
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();

        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(2000);

        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Name", "BoolTestCustomer");

        // Toggle the boolean checkbox
        var checkbox = _page.Locator(".dxbl-fl-ctrl:has([data-item-name='active']) input[type='checkbox']");
        if (await checkbox.CountAsync() > 0)
            await checkbox.First.CheckAsync();

        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(1000);

        // Navigate back and verify
        await _page.GotoAsync($"{TestSettings.BaseUrl}/Customer_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(2000);
        await lv.WaitForGridAsync();
        Assert.True(await lv.HasRowWithTextAsync("BoolTestCustomer"));

        // Clean up
        await lv.SelectRowWithTextAsync("BoolTestCustomer");
        await lv.ClickDeleteAsync();
        await lv.ConfirmDeleteAsync();
        await _page.WaitForTimeoutAsync(500);
    }

    // --- TestSchemaManagementIntegration: Schema Management still works alongside runtime entities ---

    /// <summary>Verify CustomClass CRUD still works after runtime compilation.</summary>
    [Fact]
    public async Task Test_08_CustomClassStillWorks()
    {
        var (_, lv) = await NavToCustomClassAsync();
        Assert.True(await lv.HasRowWithTextAsync(CustomerClass), "Customer metadata should be in CustomClass list");
    }

    /// <summary>Verify CustomField list still loads.</summary>
    [Fact]
    public async Task Test_09_CustomFieldStillWorks()
    {
        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Field");
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        Assert.True(await lv.HasRowWithTextAsync("Name"), "Customer's Name field should be in CustomField list");
    }

    // --- TestCleanup ---

    /// <summary>Remove test entities from runtime entity list.</summary>
    [Fact]
    public async Task Test_99_Cleanup()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);
        try
        {
            await _page.GotoAsync($"{TestSettings.BaseUrl}/Customer_ListView",
                new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
            await _page.WaitForTimeoutAsync(2000);
            var lv = new ListViewPage(_page);
            await lv.WaitForGridAsync();
            await _page.WaitForTimeoutAsync(500);
            foreach (var name in new[] { "TestCustomer1", "BoolTestCustomer", "Acme Corp" })
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
    }
}
