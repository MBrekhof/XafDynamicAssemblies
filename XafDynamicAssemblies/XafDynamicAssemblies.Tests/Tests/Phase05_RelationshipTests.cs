using XafDynamicAssemblies.Tests.Fixtures;
using XafDynamicAssemblies.Tests.Helpers;
using XafDynamicAssemblies.Tests.Pages;

namespace XafDynamicAssemblies.Tests.Tests;

/// <summary>
/// Phase 5 Tests: Entity Relationships — runtime entities can reference other entities.
/// Ported from tests/tests/test_phase5_relationships.py.
///
/// Verifies that creating a CustomField with TypeName="Reference" and ReferencedClassName
/// generates FK properties, navigation properties, and real PostgreSQL FK constraints.
/// </summary>
[Collection("Sequential")]
public class Phase05_RelationshipTests : IAsyncLifetime
{
    private readonly BrowserFixture _fixture;
    private IPage _page = null!;

    public Phase05_RelationshipTests(BrowserFixture fixture) => _fixture = fixture;

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

    /// <summary>Create a CustomClass via the UI, then return to the list view.</summary>
    private async Task CreateClassViaUiAsync(string className, string navGroup, string description = "")
    {
        await NavToCustomClassAsync();
        await DeleteIfExistsAsync(className);

        var lv = new ListViewPage(_page);
        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(2000);
        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Class Name", className);
        await detail.FillFieldAsync("Navigation Group", navGroup);
        if (!string.IsNullOrEmpty(description))
            await detail.FillFieldAsync("Description", description);
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(2000);

        // Navigate back to list view to reset page state
        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
    }

    // --- TestRelationshipSetup: create Department and Employee classes with a reference relationship ---

    /// <summary>Create RelDepartment class.</summary>
    [Fact]
    public async Task Test_01_CreateDepartmentClass()
    {
        await CreateClassViaUiAsync("RelDepartment", "Organization", "Department for relationship test");
    }

    /// <summary>Create RelEmployee class.</summary>
    [Fact]
    public async Task Test_02_CreateEmployeeClass()
    {
        await CreateClassViaUiAsync("RelEmployee", "Organization", "Employee for relationship test");
    }

    /// <summary>Add fields including a reference field via direct DB insert.</summary>
    [Fact]
    public async Task Test_03_AddFieldsViaDb()
    {
        DatabaseHelper.InsertFieldViaDb("RelDepartment", "DeptName", "System.String", isDefault: true);
        DatabaseHelper.InsertFieldViaDb("RelEmployee", "EmpName", "System.String", isDefault: true);
        DatabaseHelper.InsertFieldViaDb("RelEmployee", "Department", "Reference",
            referencedClassName: "RelDepartment");

        // Verify via Custom Field list
        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Field");
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        await _page.WaitForTimeoutAsync(500);
        Assert.True(await lv.HasRowWithTextAsync("DeptName"), "DeptName field should exist");
        Assert.True(await lv.HasRowWithTextAsync("EmpName"), "EmpName field should exist");
        Assert.True(await lv.HasRowWithTextAsync("Department"), "Department reference field should exist");
    }

    /// <summary>Deploy schema changes and wait for server restart.</summary>
    [Fact]
    public async Task Test_04_DeployAndRestart()
    {
        var (nav, lv) = await NavToCustomClassAsync();
        Assert.True(await lv.HasRowWithTextAsync("RelDepartment"), "RelDepartment should be in list");
        Assert.True(await lv.HasRowWithTextAsync("RelEmployee"), "RelEmployee should be in list");

        await ServerHelper.ClickDeploySchemaAsync(_page);
        await ServerHelper.WaitForDeployRestartAsync(_page);

        // Verify Organization nav group exists
        var links = await _page.Locator(".xaf-nav-link").AllTextContentsAsync();
        Assert.True(links.Contains("Organization"), $"Organization nav group should exist. Links: {string.Join(", ", links)}");
    }

    // --- TestRelationshipFunctionality: verify FK relationship works in the deployed runtime entities ---

    /// <summary>Create a Department record to reference.</summary>
    [Fact]
    public async Task Test_05_CreateDepartmentRecord()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);

        // Use direct URL navigation for runtime entity views after restart
        // (JS click on nav links doesn't trigger Blazor client-side routing)
        await _page.GotoAsync($"{TestSettings.BaseUrl}/RelDepartment_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(3000);
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();

        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(2000);
        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Dept Name", "Engineering");
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(2000);

        await _page.GotoAsync($"{TestSettings.BaseUrl}/RelDepartment_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(2000);
        await lv.WaitForGridAsync();
        await _page.WaitForTimeoutAsync(500);
        Assert.True(await lv.HasRowWithTextAsync("Engineering"), "Engineering department should exist");
    }

    /// <summary>Create an Employee referencing the Engineering department.</summary>
    [Fact]
    public async Task Test_06_CreateEmployeeWithReference()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);

        // Use direct URL navigation for runtime entity views after restart
        await _page.GotoAsync($"{TestSettings.BaseUrl}/RelEmployee_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(3000);
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();

        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(2000);
        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Emp Name", "Alice");
        // The Department field is a lookup — FillFieldAsync types text and tabs;
        // XAF's lookup editor should match "Engineering" from the dropdown.
        await detail.FillFieldAsync("Department", "Engineering");
        await _page.WaitForTimeoutAsync(1000);
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(2000);

        await _page.GotoAsync($"{TestSettings.BaseUrl}/RelEmployee_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(2000);
        await lv.WaitForGridAsync();
        await _page.WaitForTimeoutAsync(500);
        Assert.True(await lv.HasRowWithTextAsync("Alice"), "Alice employee should exist");
    }

    /// <summary>
    /// Verify the FK constraint was created in PostgreSQL.
    ///
    /// The Python original queries information_schema.table_constraints for RelEmployee and
    /// asserts a constraint name containing "Department" exists. SchemaSynchronizer.AddForeignKeyConstraints
    /// names constraints "FK_{ClassName}_{FieldName}" (i.e. "FK_RelEmployee_Department") and creates the FK
    /// column as "{FieldName}Id" (i.e. "DepartmentId"). DatabaseHelper.ForeignKeyExists(table, column) joins
    /// table_constraints to key_column_usage and checks the FK column directly — a strictly more precise
    /// check than Python's constraint-name substring match (it confirms the constraint is actually on the
    /// DepartmentId column, not merely that some constraint name happens to contain "Department"), so it is
    /// used as-is with no local helper needed.
    /// </summary>
    [Fact]
    public void Test_07_FkConstraintExists()
    {
        Assert.True(DatabaseHelper.ForeignKeyExists("RelEmployee", "DepartmentId"),
            "FK constraint for Department (column DepartmentId) should exist on RelEmployee");
    }

    // --- TestCleanup ---

    /// <summary>Remove test entities.</summary>
    [Fact]
    public async Task Test_99_Cleanup()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);

        // Delete Employee records first (FK dependency)
        try
        {
            await _page.GotoAsync($"{TestSettings.BaseUrl}/RelEmployee_ListView",
                new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
            await _page.WaitForTimeoutAsync(2000);
            var lv = new ListViewPage(_page);
            await lv.WaitForGridAsync();
            await _page.WaitForTimeoutAsync(500);
            foreach (var name in new[] { "Alice" })
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

        // Delete Department records
        try
        {
            await _page.GotoAsync($"{TestSettings.BaseUrl}/RelDepartment_ListView",
                new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
            await _page.WaitForTimeoutAsync(2000);
            var lv = new ListViewPage(_page);
            await lv.WaitForGridAsync();
            await _page.WaitForTimeoutAsync(500);
            foreach (var name in new[] { "Engineering" })
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

        // Delete metadata classes (cascade deletes fields)
        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        var lv2 = new ListViewPage(_page);
        await lv2.WaitForGridAsync();
        foreach (var name in new[] { "RelEmployee", "RelDepartment" })
        {
            await DeleteIfExistsAsync(name);
        }
    }
}
