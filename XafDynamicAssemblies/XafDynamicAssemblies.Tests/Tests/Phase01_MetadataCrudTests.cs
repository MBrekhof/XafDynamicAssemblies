using XafDynamicAssemblies.Tests.Fixtures;
using XafDynamicAssemblies.Tests.Pages;

namespace XafDynamicAssemblies.Tests.Tests;

/// <summary>
/// Phase 1 Tests: CustomClass and CustomField CRUD operations.
/// Ported from tests/tests/test_phase1_metadata_crud.py.
///
/// Tests are ordered and run sequentially. Each test gets a fresh browser context
/// (page) but the database persists across tests.
/// </summary>
[Collection("Sequential")]
public class Phase01_MetadataCrudTests : IAsyncLifetime
{
    private readonly BrowserFixture _fixture;
    private IPage _page = null!;

    // Cross-test state — fixed entity names shared across the ordered CRUD sequence,
    // matching Python's reliance on repeated string literals across test methods.
    private static readonly string CrudTestClass = "CrudTestClass";
    private static readonly string CrudTestClass2 = "CrudTestClass2";
    private static readonly string TestFieldName = "TestFieldName";

    public Phase01_MetadataCrudTests(BrowserFixture fixture) => _fixture = fixture;

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

    /// <summary>Navigate to Custom Field ListView and wait for grid.</summary>
    private async Task<(NavigationPage Nav, ListViewPage Lv)> NavToCustomFieldAsync()
    {
        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Field");
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        return (nav, lv);
    }

    /// <summary>Helper: create a CustomClass and return to the list view.</summary>
    private async Task<(NavigationPage Nav, ListViewPage Lv)> CreateCustomClassAsync(
        string className, string navGroup = "", string description = "")
    {
        var (nav, lv) = await NavToCustomClassAsync();
        await lv.ClickNewAsync();
        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Class Name", className);
        if (!string.IsNullOrEmpty(navGroup))
            await detail.FillFieldAsync("Navigation Group", navGroup);
        if (!string.IsNullOrEmpty(description))
            await detail.FillFieldAsync("Description", description);
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(2000);
        // Navigate back to list
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        await lv.WaitForGridAsync();
        await _page.WaitForTimeoutAsync(1000);
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

    // --- TestCustomClassCRUD ---

    /// <summary>Verify Custom Class ListView loads under Schema Management.</summary>
    [Fact]
    public async Task Test_01_NavigateToCustomClass()
    {
        await NavToCustomClassAsync();
        Assert.True(await _page.Locator(".dxbl-grid").CountAsync() > 0);
    }

    /// <summary>Create a new CustomClass and verify it appears in the list.</summary>
    [Fact]
    public async Task Test_02_CreateCustomClass()
    {
        await NavToCustomClassAsync();
        // Clean up if leftover from previous run
        await DeleteIfExistsAsync(CrudTestClass);

        var (_, lv) = await CreateCustomClassAsync(CrudTestClass, "TestGroup", "Test description");
        Assert.True(await lv.HasRowWithTextAsync(CrudTestClass));
    }

    /// <summary>Open an existing CustomClass and verify field values.</summary>
    [Fact]
    public async Task Test_03_ReadCustomClass()
    {
        var (_, lv) = await NavToCustomClassAsync();
        await lv.DoubleClickRowWithTextAsync(CrudTestClass);

        var detail = new DetailViewPage(_page);
        Assert.Equal(CrudTestClass, await detail.GetFieldValueAsync("Class Name"));
        Assert.Equal("TestGroup", await detail.GetFieldValueAsync("Navigation Group"));
        Assert.Contains("Test description", await detail.GetFieldValueAsync("Description"));
    }

    /// <summary>Verify new CustomClass has Status = Runtime by default.</summary>
    [Fact]
    public async Task Test_04_StatusDefaultsToRuntime()
    {
        var (_, lv) = await NavToCustomClassAsync();
        await lv.DoubleClickRowWithTextAsync(CrudTestClass);

        var detail = new DetailViewPage(_page);
        var statusText = await detail.GetFieldTextAsync("Status");
        Assert.Contains("Runtime", statusText);
    }

    /// <summary>Edit a CustomClass description and verify the change persists.</summary>
    [Fact]
    public async Task Test_05_EditCustomClass()
    {
        var (nav, lv) = await NavToCustomClassAsync();
        await lv.DoubleClickRowWithTextAsync(CrudTestClass);

        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Description", "Updated description");
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(500);

        // Navigate back and reopen
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        await lv.WaitForGridAsync();
        await lv.DoubleClickRowWithTextAsync(CrudTestClass);
        Assert.Contains("Updated description", await detail.GetFieldValueAsync("Description"));
    }

    /// <summary>Create a second CustomClass to verify multiple classes coexist.</summary>
    [Fact]
    public async Task Test_06_CreateSecondCustomClass()
    {
        await NavToCustomClassAsync();
        await DeleteIfExistsAsync(CrudTestClass2);

        var (_, lv) = await CreateCustomClassAsync(CrudTestClass2, "HR");
        Assert.True(await lv.HasRowWithTextAsync(CrudTestClass2));
        Assert.True(await lv.HasRowWithTextAsync(CrudTestClass));
    }

    /// <summary>Delete a CustomClass and verify removal.</summary>
    [Fact]
    public async Task Test_07_DeleteCustomClass()
    {
        var (_, lv) = await NavToCustomClassAsync();
        await DeleteIfExistsAsync(CrudTestClass2);
        Assert.False(await lv.HasRowWithTextAsync(CrudTestClass2));
    }

    // --- TestCustomFieldCRUD ---

    /// <summary>Verify Custom Field ListView loads.</summary>
    [Fact]
    public async Task Test_08_NavigateToCustomField()
    {
        await NavToCustomFieldAsync();
        Assert.True(await _page.Locator(".dxbl-grid").CountAsync() > 0);
    }

    /// <summary>Create a CustomField and verify it appears.</summary>
    [Fact]
    public async Task Test_09_CreateCustomField()
    {
        var (nav, lv) = await NavToCustomFieldAsync();
        await DeleteIfExistsAsync(TestFieldName);

        await lv.ClickNewAsync();
        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Field Name", TestFieldName);
        await detail.FillFieldAsync("Description", "A test field");
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(1000);

        await nav.NavigateToAsync("Schema Management", "Custom Field");
        await lv.WaitForGridAsync();
        await _page.WaitForTimeoutAsync(500);
        Assert.True(await lv.HasRowWithTextAsync(TestFieldName));
    }

    /// <summary>Delete a CustomField and verify removal.</summary>
    [Fact]
    public async Task Test_10_DeleteCustomField()
    {
        var (_, lv) = await NavToCustomFieldAsync();
        await DeleteIfExistsAsync(TestFieldName);
        Assert.False(await lv.HasRowWithTextAsync(TestFieldName));
    }

    // --- TestCleanup ---

    /// <summary>Remove all test data created during tests.</summary>
    [Fact]
    public async Task Test_99_Cleanup()
    {
        await NavToCustomClassAsync();
        foreach (var name in new[] { CrudTestClass, CrudTestClass2, "TestProduct", "TestProduct2" })
        {
            await DeleteIfExistsAsync(name);
        }
    }
}
