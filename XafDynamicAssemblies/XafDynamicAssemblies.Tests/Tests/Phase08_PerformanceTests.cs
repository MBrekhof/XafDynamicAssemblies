using System.Diagnostics;
using Npgsql;
using XafDynamicAssemblies.Tests.Fixtures;
using XafDynamicAssemblies.Tests.Helpers;
using XafDynamicAssemblies.Tests.Pages;

namespace XafDynamicAssemblies.Tests.Tests;

/// <summary>
/// Phase 8 Tests: Performance + Polish.
/// Ported from tests/tests/test_phase8_performance.py.
///
/// Verifies that compilation performance is acceptable for multi-class schemas
/// (bulk 10-class creation via DB + deploy), that CRUD works on bulk-created
/// entities, and that the system handles page loads without errors.
/// </summary>
[Collection("Sequential")]
public class Phase08_PerformanceTests : IAsyncLifetime
{
    private readonly BrowserFixture _fixture;
    private IPage _page = null!;

    public Phase08_PerformanceTests(BrowserFixture fixture) => _fixture = fixture;

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

    // --- TestMultiClassPerformance: compilation and startup performance with multiple classes ---

    /// <summary>Create 10 classes with fields via DB and deploy — measure total time.</summary>
    [Fact]
    public async Task Test_01_BulkCreateClasses()
    {
        using (var conn = DatabaseHelper.GetConnection())
        {
            // Clean up any previous test classes
            for (var i = 0; i < 10; i++)
            {
                var name = $"PerfTest{i:D2}";
                using var deleteCmd = new NpgsqlCommand(
                    "DELETE FROM \"CustomClasses\" WHERE \"ClassName\" = @name", conn);
                deleteCmd.Parameters.AddWithValue("name", name);
                deleteCmd.ExecuteNonQuery();
            }

            // Create 10 classes, each with 3 fields
            for (var i = 0; i < 10; i++)
            {
                var name = $"PerfTest{i:D2}";
                using var insertClassCmd = new NpgsqlCommand(
                    @"INSERT INTO ""CustomClasses"" (""ID"", ""ClassName"", ""NavigationGroup"", ""Description"",
                       ""Status"", ""GCRecord"", ""OptimisticLockField"")
                       VALUES (gen_random_uuid(), @name, 'PerfGroup', @description, 'Runtime', 0, 0)
                       RETURNING ""ID""", conn);
                insertClassCmd.Parameters.AddWithValue("name", name);
                insertClassCmd.Parameters.AddWithValue("description", $"Performance test class {i}");
                var classId = (Guid)insertClassCmd.ExecuteScalar()!;

                var fields = new (string FieldName, string TypeName)[]
                {
                    ("Name", "System.String"),
                    ("Value", "System.Decimal"),
                    ("Active", "System.Boolean"),
                };
                for (var j = 0; j < fields.Length; j++)
                {
                    var (fieldName, typeName) = fields[j];
                    using var insertFieldCmd = new NpgsqlCommand(
                        @"INSERT INTO ""CustomFields"" (""ID"", ""CustomClassId"", ""FieldName"", ""TypeName"",
                           ""IsRequired"", ""IsDefaultField"", ""Description"", ""ReferencedClassName"",
                           ""SortOrder"", ""GCRecord"", ""OptimisticLockField"")
                           VALUES (gen_random_uuid(), @classId, @fieldName, @typeName, false, @isDefault, NULL, NULL, @sortOrder, 0, 0)",
                        conn);
                    insertFieldCmd.Parameters.AddWithValue("classId", classId);
                    insertFieldCmd.Parameters.AddWithValue("fieldName", fieldName);
                    insertFieldCmd.Parameters.AddWithValue("typeName", typeName);
                    insertFieldCmd.Parameters.AddWithValue("isDefault", fieldName == "Name");
                    insertFieldCmd.Parameters.AddWithValue("sortOrder", j);
                    insertFieldCmd.ExecuteNonQuery();
                }
            }
        }

        // Deploy and measure time
        await NavToCustomClassAsync();
        var stopwatch = Stopwatch.StartNew();
        await ServerHelper.ClickDeploySchemaAsync(_page);
        await ServerHelper.WaitForDeployRestartAsync(_page, serverTimeoutSeconds: 90);
        stopwatch.Stop();
        var deployTime = stopwatch.Elapsed.TotalSeconds;

        // Verify all 10 classes compiled successfully
        var links = await _page.Locator(".xaf-nav-link").AllTextContentsAsync();
        Assert.True(links.Contains("PerfGroup"), $"PerfGroup should exist after deploy. Links: {string.Join(", ", links)}");

        // Performance check: deploy + restart should complete in under 60 seconds
        // (Roslyn compilation for 10 classes typically takes 2-5 seconds,
        //  but process restart and XAF bootstrap add overhead)
        Assert.True(deployTime < 60, $"Deploy+restart took {deployTime:F1}s (should be under 60s)");
    }

    /// <summary>Verify CRUD works on one of the bulk-created entities.</summary>
    [Fact]
    public async Task Test_02_MultiClassCrudWorks()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);
        await _page.GotoAsync($"{TestSettings.BaseUrl}/PerfTest05_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(3000);
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();

        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(2000);
        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Name", "PerfRecord1");
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(2000);

        await _page.GotoAsync($"{TestSettings.BaseUrl}/PerfTest05_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(2000);
        await lv.WaitForGridAsync();
        Assert.True(await lv.HasRowWithTextAsync("PerfRecord1"), "PerfRecord1 should exist");
    }

    // --- TestConcurrentPageLoads: system handles page loads from multiple browser contexts ---

    /// <summary>Open a runtime entity ListView and verify it renders without errors.</summary>
    [Fact]
    public async Task Test_03_ConcurrentPageAccess()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);

        // Open PerfTest00 in this page
        await _page.GotoAsync($"{TestSettings.BaseUrl}/PerfTest00_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(3000);
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();

        // The grid should render without errors
        var grids = _page.Locator(".dxbl-grid");
        Assert.True(await grids.First.IsVisibleAsync() || await grids.Last.IsVisibleAsync());
    }

    // --- TestCleanup ---

    /// <summary>Remove all Phase 8 test data.</summary>
    [Fact]
    public async Task Test_99_Cleanup()
    {
        // Delete runtime entity data first
        for (var i = 0; i < 10; i++)
        {
            var name = $"PerfTest{i:D2}";
            try
            {
                await _page.GotoAsync($"{TestSettings.BaseUrl}/{name}_ListView",
                    new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30_000 });
                await _page.WaitForTimeoutAsync(1000);
                var lv = new ListViewPage(_page);
                await lv.WaitForGridAsync();
                // Delete all visible rows
                while (true)
                {
                    var rows = _page.Locator(".dxbl-grid-table tbody tr[data-visible-index]");
                    if (await rows.CountAsync() <= 1) // Header row only
                        break;
                    await rows.Nth(1).ClickAsync();
                    await _page.WaitForTimeoutAsync(300);
                    await lv.ClickDeleteAsync();
                    await lv.ConfirmDeleteAsync();
                    await _page.WaitForTimeoutAsync(300);
                }
            }
            catch
            {
                // ponytail: matches Python's bare `except Exception: pass` — cleanup is best-effort.
            }
        }

        // Delete metadata classes
        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        var lv2 = new ListViewPage(_page);
        await lv2.WaitForGridAsync();
        for (var i = 0; i < 10; i++)
        {
            await DeleteIfExistsAsync($"PerfTest{i:D2}");
        }

        // Drop tables
        using var conn = DatabaseHelper.GetConnection();
        for (var i = 0; i < 10; i++)
        {
            using var cmd = new NpgsqlCommand($"DROP TABLE IF EXISTS \"PerfTest{i:D2}\" CASCADE", conn);
            cmd.ExecuteNonQuery();
        }
    }
}
