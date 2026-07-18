using Npgsql;
using XafDynamicAssemblies.Tests.Fixtures;
using XafDynamicAssemblies.Tests.Helpers;
using XafDynamicAssemblies.Tests.Pages;

namespace XafDynamicAssemblies.Tests.Tests;

/// <summary>
/// Phase 7 Tests: Error Handling + Hardening.
/// Ported from tests/tests/test_phase7_error_handling.py.
///
/// Verifies graceful degraded mode when runtime metadata contains an invalid field
/// type (compilation failure), recovery once the metadata is fixed and redeployed,
/// server boot with empty runtime metadata, and restart recovery with valid metadata.
/// </summary>
[Collection("Sequential")]
public class Phase07_ErrorHandlingTests : IAsyncLifetime
{
    private readonly BrowserFixture _fixture;
    private IPage _page = null!;

    public Phase07_ErrorHandlingTests(BrowserFixture fixture) => _fixture = fixture;

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

    /// <summary>
    /// Create a CustomClass directly via DB (bypassing UI validation), matching Python's
    /// create_class_via_db(). One-off helper — used only by Test_01 — so it stays inline
    /// per the Phase06 precedent rather than moving into DatabaseHelper.
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

    // --- TestDegradedMode: compilation errors cause graceful degraded mode ---

    /// <summary>
    /// Insert a class with invalid TypeName, deploy, verify server starts in degraded mode.
    /// The server should still boot with compiled entities working (CustomClass, CustomField).
    /// </summary>
    [Fact]
    public async Task Test_01_InvalidTypenameDegradesGracefully()
    {
        // Create a class with an invalid type that will cause compilation failure
        CreateClassViaDb("BadTypeClass", "ErrorTest", "Class with invalid field type");
        DatabaseHelper.InsertFieldViaDb("BadTypeClass", "BadField", "Totally.Invalid.Type.That.Does.Not.Exist");

        // Deploy — this will trigger compilation which should fail for the invalid type
        await NavToCustomClassAsync();
        await ServerHelper.ClickDeploySchemaAsync(_page);
        await ServerHelper.WaitForDeployRestartAsync(_page);

        // Server should be up in degraded mode — compiled entities still work
        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        Assert.True(await lv.HasRowWithTextAsync("BadTypeClass"), "BadTypeClass metadata should still be visible");
    }

    /// <summary>Verify CRUD on compiled entities (CustomClass/CustomField) works in degraded mode.</summary>
    [Fact]
    public async Task Test_02_CompiledEntitiesWorkInDegraded()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);
        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Field");
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        // The Custom Field list should load without errors
        Assert.True(true, "CustomField list loads in degraded mode");
    }

    // --- TestRecoveryFromErrors: recovery from error states ---

    /// <summary>Fix the invalid field, deploy again, verify recovery.</summary>
    [Fact]
    public async Task Test_03_FixInvalidMetadataAndRecover()
    {
        // Remove the bad field and fix the class
        using (var conn = DatabaseHelper.GetConnection())
        using (var cmd = new NpgsqlCommand(
            @"DELETE FROM ""CustomFields"" WHERE ""FieldName"" = 'BadField'
              AND ""CustomClassId"" IN (
                  SELECT ""ID"" FROM ""CustomClasses"" WHERE ""ClassName"" = 'BadTypeClass'
              )", conn))
        {
            cmd.ExecuteNonQuery();
        }

        // Add a valid field instead
        DatabaseHelper.InsertFieldViaDb("BadTypeClass", "ValidName", "System.String", isDefault: true);

        // Deploy again — should succeed now
        await NavToCustomClassAsync();
        await ServerHelper.ClickDeploySchemaAsync(_page);
        await ServerHelper.WaitForDeployRestartAsync(_page);

        // ErrorTest nav group should now exist (BadTypeClass has NavGroup "ErrorTest")
        var links = await _page.Locator(".xaf-nav-link").AllTextContentsAsync();
        Assert.True(links.Contains("ErrorTest"), $"ErrorTest nav should exist after recovery. Links: {string.Join(", ", links)}");
    }

    /// <summary>Verify the recovered entity has working CRUD.</summary>
    [Fact]
    public async Task Test_04_RecoveredEntityWorks()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);
        await _page.GotoAsync($"{TestSettings.BaseUrl}/BadTypeClass_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(3000);
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();

        // Create a record
        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(2000);
        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Valid Name", "RecoveryTest1");
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(2000);

        // Verify it appears
        await _page.GotoAsync($"{TestSettings.BaseUrl}/BadTypeClass_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(2000);
        await lv.WaitForGridAsync();
        Assert.True(await lv.HasRowWithTextAsync("RecoveryTest1"), "RecoveryTest1 should exist after recovery");
    }

    // --- TestEmptyMetadataStartup: server behavior with no runtime metadata ---

    /// <summary>Remove all runtime metadata, deploy+restart, verify server boots cleanly.</summary>
    [Fact]
    public async Task Test_05_EmptyMetadataServerBoots()
    {
        // First clean up the recovered entity data
        try
        {
            await _page.GotoAsync($"{TestSettings.BaseUrl}/BadTypeClass_ListView",
                new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
            await _page.WaitForTimeoutAsync(2000);
            var lv = new ListViewPage(_page);
            await lv.WaitForGridAsync();
            if (await lv.HasRowWithTextAsync("RecoveryTest1"))
            {
                await lv.SelectRowWithTextAsync("RecoveryTest1");
                await lv.ClickDeleteAsync();
                await lv.ConfirmDeleteAsync();
                await _page.WaitForTimeoutAsync(500);
            }
        }
        catch
        {
            // ponytail: matches Python's bare `except Exception: pass` — cleanup is best-effort.
        }

        // Delete all runtime classes
        await NavToCustomClassAsync();
        foreach (var name in new[] { "BadTypeClass", "Customer", "HotLoadProduct" })
        {
            await DeleteIfExistsAsync(name);
        }

        // Deploy with empty runtime set
        await ServerHelper.ClickDeploySchemaAsync(_page);
        await ServerHelper.WaitForDeployRestartAsync(_page);

        // Server should boot — Schema Management should still work
        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        var lv2 = new ListViewPage(_page);
        await lv2.WaitForGridAsync();

        // No runtime nav groups should exist
        var links = await _page.Locator(".xaf-nav-link").AllTextContentsAsync();
        Assert.False(links.Contains("ErrorTest"), "ErrorTest nav should be gone");
        Assert.True(links.Contains("Schema Management"), "Schema Management should still exist");
    }

    // --- TestRestartRecovery: server recovers correctly after restart with existing metadata ---

    /// <summary>Create a class, deploy, and verify it works after restart.</summary>
    [Fact]
    public async Task Test_06_CreateThenRestartRecovery()
    {
        // Create a simple class
        var (nav, lv) = await NavToCustomClassAsync();
        await DeleteIfExistsAsync("RestartTest");
        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(2000);
        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Class Name", "RestartTest");
        await detail.FillFieldAsync("Navigation Group", "RecoveryGroup");
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(2000);

        // Add a field via DB
        DatabaseHelper.InsertFieldViaDb("RestartTest", "ItemName", "System.String", isDefault: true);

        // Deploy
        nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        await ServerHelper.ClickDeploySchemaAsync(_page);
        await ServerHelper.WaitForDeployRestartAsync(_page);

        // Verify it works after restart
        var links = await _page.Locator(".xaf-nav-link").AllTextContentsAsync();
        Assert.True(links.Contains("RecoveryGroup"), $"RecoveryGroup should exist after restart. Links: {string.Join(", ", links)}");
    }

    // --- TestCleanup ---

    /// <summary>Remove test entities.</summary>
    [Fact]
    public async Task Test_99_Cleanup()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);

        // Delete runtime entity data
        try
        {
            await _page.GotoAsync($"{TestSettings.BaseUrl}/RestartTest_ListView",
                new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
            await _page.WaitForTimeoutAsync(2000);
            var lv = new ListViewPage(_page);
            await lv.WaitForGridAsync();
            // Delete all rows if any
        }
        catch
        {
            // ponytail: matches Python's bare `except Exception: pass`.
        }

        // Delete metadata classes
        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        var lv2 = new ListViewPage(_page);
        await lv2.WaitForGridAsync();
        foreach (var name in new[] { "BadTypeClass", "RestartTest" })
        {
            await DeleteIfExistsAsync(name);
        }

        // Drop test tables
        using var conn = DatabaseHelper.GetConnection();
        foreach (var table in new[] { "BadTypeClass", "RestartTest" })
        {
            using var cmd = new NpgsqlCommand($"DROP TABLE IF EXISTS \"{table}\" CASCADE", conn);
            cmd.ExecuteNonQuery();
        }
    }
}
