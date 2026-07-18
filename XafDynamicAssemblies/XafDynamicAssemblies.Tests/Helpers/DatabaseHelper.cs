using Npgsql;

namespace XafDynamicAssemblies.Tests.Helpers;

/// <summary>
/// Direct PostgreSQL access for test setup/verification, ported from the Python test suite's
/// get_db_connection()/insert_field_via_db() helpers (duplicated across tests/tests/test_phase*.py).
///
/// Ground truth for table/column names is the live schema (verified via `\d "CustomClasses"` /
/// `\d "CustomFields"`), not the entity class names: CustomClass/CustomField derive from
/// DevExpress.Persistent.BaseImpl.EF.BaseObject, whose PK property is "ID" (not "Id"), and the
/// DbContext declares `DbSet&lt;CustomClass&gt; CustomClasses` / `DbSet&lt;CustomField&gt; CustomFields`
/// with no ToTable() override, so EF Core's convention pluralizes the table names.
/// </summary>
public static class DatabaseHelper
{
    public static NpgsqlConnection GetConnection()
    {
        var connStr = $"Host={TestSettings.DbHost};Port={TestSettings.DbPort};" +
                      $"Database={TestSettings.DbName};Username={TestSettings.DbUser};" +
                      $"Password={TestSettings.DbPassword}";
        var conn = new NpgsqlConnection(connStr);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Insert a CustomField row directly via PostgreSQL, bypassing the XAF UI.
    /// Parameter set/order mirrors the most complete Python variant
    /// (test_phase9_review_fixes.py::insert_field_via_db), which is a superset of the
    /// simpler duplicates in the other phase test files.
    /// </summary>
    public static void InsertFieldViaDb(
        string className, string fieldName, string typeName = "System.String",
        string? referencedClassName = null, bool isDefault = false,
        bool isRequired = false, bool isImmediatePostData = false,
        int? stringMaxLength = null, bool isVisibleInListView = true,
        bool isVisibleInDetailView = true, bool isEditable = true,
        string? toolTip = null, string? displayName = null)
    {
        using var conn = GetConnection();

        // Find the CustomClass ID (only among non-deleted rows — GCRecord is XAF's
        // deferred-deletion soft-delete marker).
        using var findCmd = new NpgsqlCommand(
            "SELECT \"ID\" FROM \"CustomClasses\" WHERE \"ClassName\" = @name " +
            "AND (\"GCRecord\" IS NULL OR \"GCRecord\" = 0)", conn);
        findCmd.Parameters.AddWithValue("name", className);
        var classId = (Guid?)findCmd.ExecuteScalar()
            ?? throw new Exception($"CustomClass '{className}' not found");

        // Delete any existing field with the same name so re-running a test is idempotent.
        using var deleteCmd = new NpgsqlCommand(
            "DELETE FROM \"CustomFields\" WHERE \"CustomClassId\" = @classId AND \"FieldName\" = @fieldName", conn);
        deleteCmd.Parameters.AddWithValue("classId", classId);
        deleteCmd.Parameters.AddWithValue("fieldName", fieldName);
        deleteCmd.ExecuteNonQuery();

        // Insert the field. GCRecord/OptimisticLockField have DB defaults (0) but are set
        // explicitly for parity with the Python source. IsRequired and SortOrder are NOT NULL
        // with no DB default, so they must always be supplied.
        using var insertCmd = new NpgsqlCommand(@"
            INSERT INTO ""CustomFields"" (""ID"", ""CustomClassId"", ""FieldName"", ""TypeName"",
                ""IsRequired"", ""IsDefaultField"", ""Description"", ""ReferencedClassName"",
                ""SortOrder"", ""GCRecord"", ""OptimisticLockField"",
                ""IsImmediatePostData"", ""StringMaxLength"",
                ""IsVisibleInListView"", ""IsVisibleInDetailView"",
                ""IsEditable"", ""ToolTip"", ""DisplayName"")
            VALUES (@id, @classId, @fieldName, @typeName, @isRequired, @isDefault, NULL, @refClass,
                0, 0, 0, @immediatePostData, @maxLength, @visList, @visDetail, @editable, @toolTip, @displayName)", conn);
        insertCmd.Parameters.AddWithValue("id", Guid.NewGuid());
        insertCmd.Parameters.AddWithValue("classId", classId);
        insertCmd.Parameters.AddWithValue("fieldName", fieldName);
        insertCmd.Parameters.AddWithValue("typeName", typeName);
        insertCmd.Parameters.AddWithValue("isRequired", isRequired);
        insertCmd.Parameters.AddWithValue("isDefault", isDefault);
        insertCmd.Parameters.AddWithValue("refClass", (object?)referencedClassName ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("immediatePostData", isImmediatePostData);
        insertCmd.Parameters.AddWithValue("maxLength", (object?)stringMaxLength ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("visList", isVisibleInListView);
        insertCmd.Parameters.AddWithValue("visDetail", isVisibleInDetailView);
        insertCmd.Parameters.AddWithValue("editable", isEditable);
        insertCmd.Parameters.AddWithValue("toolTip", (object?)toolTip ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("displayName", (object?)displayName ?? DBNull.Value);
        insertCmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Set the IsApiExposed flag on a CustomClass directly via SQL, ported from the Python
    /// test suite's set_api_exposed_via_db() (tests/tests/test_phase10_web_api.py).
    /// </summary>
    public static void SetApiExposedViaDb(string className, bool isExposed)
    {
        using var conn = GetConnection();
        using var cmd = new NpgsqlCommand(
            "UPDATE \"CustomClasses\" SET \"IsApiExposed\" = @exposed WHERE \"ClassName\" = @name " +
            "AND (\"GCRecord\" IS NULL OR \"GCRecord\" = 0)", conn);
        cmd.Parameters.AddWithValue("exposed", isExposed);
        cmd.Parameters.AddWithValue("name", className);
        var rows = cmd.ExecuteNonQuery();
        if (rows == 0)
            throw new Exception($"No CustomClass found with name '{className}'");
    }

    public static bool TableExists(string tableName)
    {
        using var conn = GetConnection();
        using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = @name", conn);
        cmd.Parameters.AddWithValue("name", tableName);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    public static bool ForeignKeyExists(string tableName, string columnName)
    {
        using var conn = GetConnection();
        using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*) FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name
              AND tc.table_schema = kcu.table_schema
            WHERE tc.constraint_type = 'FOREIGN KEY'
              AND tc.table_schema = 'public'
              AND tc.table_name = @table
              AND kcu.column_name = @col", conn);
        cmd.Parameters.AddWithValue("table", tableName);
        cmd.Parameters.AddWithValue("col", columnName);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    /// <summary>
    /// Delete a CustomClass and its CustomFields. Explicit two-statement delete (the DB also
    /// cascades CustomFields via FK_CustomFields_CustomClasses_CustomClassId ON DELETE CASCADE,
    /// but being explicit keeps this helper correct even if that constraint is ever relaxed).
    /// </summary>
    public static void DeleteCustomClass(string className)
    {
        using var conn = GetConnection();
        using var cmd = new NpgsqlCommand(@"
            DELETE FROM ""CustomFields"" WHERE ""CustomClassId"" IN
                (SELECT ""ID"" FROM ""CustomClasses"" WHERE ""ClassName"" = @name);
            DELETE FROM ""CustomClasses"" WHERE ""ClassName"" = @name;", conn);
        cmd.Parameters.AddWithValue("name", className);
        cmd.ExecuteNonQuery();
    }
}
