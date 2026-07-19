using Npgsql;
using XafDynamicAssemblies.Tests.Fixtures;
using XafDynamicAssemblies.Tests.Helpers;
using XafDynamicAssemblies.Tests.Pages;

namespace XafDynamicAssemblies.Tests.Tests;

/// <summary>
/// Repro for DATA-001: SchemaSynchronizer.GetExistingColumns/AddMissingColumns used a
/// case-INSENSITIVE existence check (StringComparer.OrdinalIgnoreCase). A stale lowercase
/// column (e.g. "email") on an existing table satisfied the existence check for metadata
/// field "Email", so `ALTER TABLE ... ADD COLUMN "Email"` never ran. EF Core maps the
/// exact-quoted "Email" column, so every query against the entity then failed.
///
/// This test manually creates that stale state (a table with a lowercase "email" column but
/// no "Email" column, mirroring what SchemaSynchronizer.CreateTable would have produced before
/// the stale column existed) and deploys — AddMissingColumns must add the correctly-cased
/// "Email" column even though "email" already exists.
/// </summary>
[Collection("Sequential")]
public class SchemaSyncCaseSensitivityTests : IAsyncLifetime
{
    private const string ClassName = "CaseSyncProbe";

    private readonly BrowserFixture _fixture;
    private IPage _page = null!;

    public SchemaSyncCaseSensitivityTests(BrowserFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _page = await _fixture.NewPageAsync();

    public async Task DisposeAsync() => await _page.Context.DisposeAsync();

    /// <summary>
    /// Create a CustomClass directly via DB (bypassing UI validation), matching the
    /// Phase07/Phase06 precedent — one-off helper used only by this test, kept inline.
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

    /// <summary>Count columns named exactly (case-sensitive) <paramref name="columnName"/> on the table.</summary>
    private static long ExactCaseColumnCount(string tableName, string columnName)
    {
        using var conn = DatabaseHelper.GetConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = @t AND column_name = @c",
            conn);
        cmd.Parameters.AddWithValue("t", tableName);
        cmd.Parameters.AddWithValue("c", columnName);
        return (long)cmd.ExecuteScalar()!;
    }

    private static void DeleteMetadataAndDropTable()
    {
        using (var conn = DatabaseHelper.GetConnection())
        {
            using (var cmd = new NpgsqlCommand(
                @"DELETE FROM ""CustomFields"" WHERE ""CustomClassId"" IN
                    (SELECT ""ID"" FROM ""CustomClasses"" WHERE ""ClassName"" = @name);
                  DELETE FROM ""CustomClasses"" WHERE ""ClassName"" = @name;", conn))
            {
                cmd.Parameters.AddWithValue("name", ClassName);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = new NpgsqlCommand($"DROP TABLE IF EXISTS \"{ClassName}\" CASCADE", conn))
            {
                cmd.ExecuteNonQuery();
            }
        }
    }

    [Fact]
    public async Task Test_01_StaleLowercaseColumn_DoesNotBlockCorrectlyCasedColumnFromBeingAdded()
    {
        // Belt-and-braces: clear any leftovers from a previous failed run before we start.
        DeleteMetadataAndDropTable();

        try
        {
            // --- Arrange: metadata for a class with one field, "Email" ---
            CreateClassViaDb(ClassName, "ErrorTest", "DATA-001 repro: stale differently-cased column");
            DatabaseHelper.InsertFieldViaDb(ClassName, "Email", "System.String", isDefault: true);

            // --- Arrange: manually create the STALE table state — same base columns
            // SchemaSynchronizer.CreateTable would generate, plus a stale lowercase "email"
            // column and deliberately NO "Email" column. TableExists() will see this table
            // and route through AddMissingColumns instead of CreateTable. ---
            using (var conn = DatabaseHelper.GetConnection())
            using (var cmd = new NpgsqlCommand($@"
                CREATE TABLE ""{ClassName}"" (
                    ""ID"" uuid NOT NULL DEFAULT gen_random_uuid(),
                    ""ObjectType"" varchar(256) NULL,
                    ""GCRecord"" integer NOT NULL DEFAULT 0,
                    ""OptimisticLockField"" integer NOT NULL DEFAULT 0,
                    email text NULL,
                    PRIMARY KEY (""ID"")
                )", conn))
            {
                cmd.ExecuteNonQuery();
            }

            // Sanity-check the stale fixture: lowercase "email" exists, "Email" does not.
            Assert.Equal(1, ExactCaseColumnCount(ClassName, "email"));
            Assert.Equal(0, ExactCaseColumnCount(ClassName, "Email"));

            // --- Act: deploy — SchemaSynchronizer.SynchronizeAll runs AddMissingColumns
            // against the existing table on restart. ---
            var nav = new NavigationPage(_page);
            await nav.NavigateToAsync("Schema Management", "Custom Class");
            var lv = new ListViewPage(_page);
            await lv.WaitForGridAsync();
            await ServerHelper.ClickDeploySchemaAsync(_page);
            await ServerHelper.WaitForDeployRestartAsync(_page);

            // --- Assert: the correctly-cased "Email" column now exists (case-sensitive
            // information_schema check — this is the bug: with OrdinalIgnoreCase, the stale
            // "email" column satisfied the existence check and the ALTER TABLE never ran). ---
            Assert.Equal(1, ExactCaseColumnCount(ClassName, "Email"));

            // --- Assert: the user-visible symptom — the entity's ListView loads without error. ---
            await _page.GotoAsync($"{TestSettings.BaseUrl}/{ClassName}_ListView",
                new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
            await _page.WaitForTimeoutAsync(3000);
            await lv.WaitForGridAsync();
            var errorLocator = _page.Locator(".dx-notification-message, .xaf-error")
                .Or(_page.GetByText("An error has occurred"));
            var errorCount = await errorLocator.CountAsync();
            Assert.Equal(0, errorCount);
        }
        finally
        {
            // --- Cleanup: remove metadata + table, then redeploy so the runtime type disappears. ---
            DeleteMetadataAndDropTable();

            await ServerHelper.ReloadAndWaitAsync(_page);
            var nav = new NavigationPage(_page);
            await nav.NavigateToAsync("Schema Management", "Custom Class");
            var lv = new ListViewPage(_page);
            await lv.WaitForGridAsync();
            await ServerHelper.ClickDeploySchemaAsync(_page);
            await ServerHelper.WaitForDeployRestartAsync(_page);
        }
    }
}
