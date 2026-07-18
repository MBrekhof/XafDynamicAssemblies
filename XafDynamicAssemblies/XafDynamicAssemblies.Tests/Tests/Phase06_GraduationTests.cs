using Npgsql;
using XafDynamicAssemblies.Tests.Fixtures;
using XafDynamicAssemblies.Tests.Helpers;
using XafDynamicAssemblies.Tests.Pages;

namespace XafDynamicAssemblies.Tests.Tests;

/// <summary>
/// Phase 6 Tests: Graduation — runtime entities exported to compiled C# source.
/// Ported from tests/tests/test_phase6_graduation.py.
///
/// Verifies that the Graduate action generates production-quality C# source,
/// changes status to Compiled, and the entity is removed from runtime compilation
/// after deploy while preserving the database table and data.
/// </summary>
[Collection("Sequential")]
public class Phase06_GraduationTests : IAsyncLifetime
{
    private readonly BrowserFixture _fixture;
    private IPage _page = null!;

    public Phase06_GraduationTests(BrowserFixture fixture) => _fixture = fixture;

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

        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
    }

    /// <summary>Locate the Graduate toolbar action button (toolbar-item, falling back to button/span text).</summary>
    private ILocator GraduateButton()
    {
        var btn = _page.Locator("dxbl-toolbar-item[text=\"Graduate\"]");
        return btn;
    }

    // --- TestGraduationSetup: create a GradTest class, add fields, deploy, and add data ---

    /// <summary>Create GradTest class for graduation testing.</summary>
    [Fact]
    public async Task Test_01_CreateGradtestClass()
    {
        await CreateClassViaUiAsync("GradTest", "GradGroup", "Test entity for graduation");
    }

    /// <summary>Add fields to GradTest class via DB.</summary>
    [Fact]
    public async Task Test_02_AddGradtestFields()
    {
        DatabaseHelper.InsertFieldViaDb("GradTest", "Title", "System.String", isDefault: true);
        DatabaseHelper.InsertFieldViaDb("GradTest", "Amount", "System.Decimal");

        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Field");
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        await _page.WaitForTimeoutAsync(500);
        Assert.True(await lv.HasRowWithTextAsync("Title"), "Title field should exist");
    }

    /// <summary>Deploy schema so GradTest becomes a runtime entity.</summary>
    [Fact]
    public async Task Test_03_DeployGradtest()
    {
        var (nav, lv) = await NavToCustomClassAsync();
        Assert.True(await lv.HasRowWithTextAsync("GradTest"), "GradTest should be in list");
        await ServerHelper.ClickDeploySchemaAsync(_page);
        await ServerHelper.WaitForDeployRestartAsync(_page);

        var links = await _page.Locator(".xaf-nav-link").AllTextContentsAsync();
        Assert.True(links.Contains("GradGroup"), $"GradGroup nav should exist after deploy. Links: {string.Join(", ", links)}");
    }

    /// <summary>Create a record in GradTest to verify data preservation after graduation.</summary>
    [Fact]
    public async Task Test_04_CreateGradtestData()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);
        await _page.GotoAsync($"{TestSettings.BaseUrl}/GradTest_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(3000);
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();

        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(2000);
        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Title", "GradTestRecord1");
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(2000);

        await _page.GotoAsync($"{TestSettings.BaseUrl}/GradTest_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(2000);
        await lv.WaitForGridAsync();
        Assert.True(await lv.HasRowWithTextAsync("GradTestRecord1"), "GradTestRecord1 should exist");
    }

    // --- TestGraduateAction: test the Graduate action on CustomClass ---

    /// <summary>Verify the Graduate action is available on CustomClass DetailView.</summary>
    [Fact]
    public async Task Test_05_GraduateActionAvailable()
    {
        var (nav, lv) = await NavToCustomClassAsync();
        await lv.DoubleClickRowWithTextAsync("GradTest");
        await _page.WaitForTimeoutAsync(2000);

        var graduateBtn = GraduateButton();
        if (await graduateBtn.CountAsync() == 0)
            graduateBtn = _page.Locator("button:has-text('Graduate'), span:has-text('Graduate')");
        Assert.True(await graduateBtn.CountAsync() > 0, "Graduate action should be available");
    }

    /// <summary>Click Graduate and verify source code is generated and status changes.</summary>
    [Fact]
    public async Task Test_06_GraduateGeneratesSource()
    {
        var (nav, lv) = await NavToCustomClassAsync();
        await lv.DoubleClickRowWithTextAsync("GradTest");
        await _page.WaitForTimeoutAsync(2000);

        // Click Graduate
        var graduateBtn = GraduateButton();
        if (await graduateBtn.CountAsync() == 0)
            graduateBtn = _page.Locator("button:has-text('Graduate'), span:has-text('Graduate')");
        await graduateBtn.First.ClickAsync();
        await _page.WaitForTimeoutAsync(1000);

        // Accept confirmation dialog
        var confirmBtn = _page.Locator("button:has-text('Yes'), button:has-text('OK')");
        if (await confirmBtn.CountAsync() > 0)
            await confirmBtn.First.ClickAsync();
        await _page.WaitForTimeoutAsync(2000);

        // Dismiss success message if shown
        var okBtn = _page.Locator("button:has-text('OK')");
        if (await okBtn.CountAsync() > 0)
        {
            await okBtn.First.ClickAsync();
            await _page.WaitForTimeoutAsync(500);
        }

        // Verify status changed to Compiled
        var detail = new DetailViewPage(_page);
        var statusValue = await detail.GetFieldValueAsync("Status");
        Assert.True(statusValue == "Compiled" || statusValue.Contains("Compiled"),
            $"Status should be Compiled, got: {statusValue}");

        // Verify graduated source was generated (check via DB since the field might
        // be rendered as a large text area)
        using var conn = DatabaseHelper.GetConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT \"GraduatedSource\", \"Status\" FROM \"CustomClasses\" WHERE \"ClassName\" = @name", conn);
        cmd.Parameters.AddWithValue("name", "GradTest");
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), "GradTest should exist in DB");
        var source = reader.IsDBNull(0) ? null : reader.GetString(0);
        var status = reader.IsDBNull(1) ? null : reader.GetString(1);
        Assert.True(!string.IsNullOrEmpty(source), "GraduatedSource should be populated");
        Assert.Contains("class GradTest", source);
        Assert.Contains("BaseObject", source);
        Assert.Contains("Title", source);
        Assert.Contains("Amount", source);
        Assert.Contains("DbContext", source);
        Assert.Contains("migration", source.ToLowerInvariant());
        Assert.Equal("Compiled", status);
    }

    // --- TestGraduationRemovesFromRuntime: verify that after graduation + deploy, the entity is removed from runtime ---

    /// <summary>Deploy schema after graduation — GradTest should be removed from runtime nav.</summary>
    [Fact]
    public async Task Test_07_DeployAfterGraduation()
    {
        var (nav, lv) = await NavToCustomClassAsync();
        await ServerHelper.ClickDeploySchemaAsync(_page);
        await ServerHelper.WaitForDeployRestartAsync(_page);

        // GradGroup nav should NOT exist (GradTest was the only class in that group)
        var links = await _page.Locator(".xaf-nav-link").AllTextContentsAsync();
        Assert.False(links.Contains("GradGroup"),
            $"GradGroup should be removed from nav after graduation. Links: {string.Join(", ", links)}");
    }

    /// <summary>Verify the GradTest table and data still exist in PostgreSQL.</summary>
    [Fact]
    public void Test_08_DataPreservedInDatabase()
    {
        Assert.True(DatabaseHelper.TableExists("GradTest"), "GradTest table should still exist in PostgreSQL");

        using var conn = DatabaseHelper.GetConnection();
        using var cmd = new NpgsqlCommand("SELECT \"Title\" FROM \"GradTest\"", conn);
        using var reader = cmd.ExecuteReader();
        var titles = new List<string?>();
        while (reader.Read())
            titles.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        Assert.True(titles.Contains("GradTestRecord1"),
            $"GradTestRecord1 should still exist in DB. Found: {string.Join(", ", titles)}");
    }

    // --- TestCleanup ---

    /// <summary>Remove test entities.</summary>
    [Fact]
    public async Task Test_99_Cleanup()
    {
        // Delete GradTest class metadata
        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        await DeleteIfExistsAsync("GradTest");

        // Drop the GradTest table
        using var conn = DatabaseHelper.GetConnection();
        using var cmd = new NpgsqlCommand("DROP TABLE IF EXISTS \"GradTest\" CASCADE", conn);
        cmd.ExecuteNonQuery();
    }
}
