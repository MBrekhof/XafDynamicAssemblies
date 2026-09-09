using Microsoft.Extensions.Logging;
using Npgsql;
using XafDynamicAssemblies.Module.BusinessObjects;

namespace XafDynamicAssemblies.Module.Services
{
    /// <summary>
    /// Executes PostgreSQL DDL to create/alter tables for runtime entities.
    /// Never drops columns — only adds. The XAF database updater is configured to match
    /// (Startup.cs: SchemaUpdateOptions.DisableAlterAndDeleteOperations, DATA-002), so a
    /// deleted runtime field leaves its column and data in place on every path.
    /// </summary>
    public class SchemaSynchronizer
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;

        public SchemaSynchronizer(string connectionString, ILogger logger = null)
        {
            _connectionString = connectionString;
            _logger = logger;
        }

        /// <summary>
        /// Synchronize all runtime entity tables based on metadata.
        /// </summary>
        public void SynchronizeAll(List<CustomClass> classes)
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();

            // DATA-003: one bad class must not block the rest; report all failures at the end.
            var failures = new List<string>();
            foreach (var cc in classes)
            {
                try
                {
                    SynchronizeTable(conn, cc);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "DDL sync failed for {ClassName}", cc.ClassName);
                    failures.Add($"{cc.ClassName}: {ex.Message}");
                }
            }

            if (failures.Count > 0)
                throw new InvalidOperationException($"DDL sync failed for {failures.Count} class(es): {string.Join(" | ", failures)}");
        }

        private void SynchronizeTable(NpgsqlConnection conn, CustomClass cc)
        {
            var tableName = QuoteIdentifier(cc.ClassName);

            if (!TableExists(conn, cc.ClassName))
            {
                CreateTable(conn, cc);
            }
            else
            {
                AddMissingColumns(conn, cc);
            }
        }

        private void CreateTable(NpgsqlConnection conn, CustomClass cc)
        {
            var tableName = QuoteIdentifier(cc.ClassName);
            var columns = new List<string>
            {
                "\"ID\" uuid NOT NULL DEFAULT gen_random_uuid()",
                "\"ObjectType\" varchar(256) NULL",
                "\"GCRecord\" integer NOT NULL DEFAULT 0",
                "\"OptimisticLockField\" integer NOT NULL DEFAULT 0"
            };

            // Add custom columns
            foreach (var field in cc.Fields.Where(f => !string.IsNullOrWhiteSpace(f.FieldName)))
            {
                if (IsReferenceField(field))
                {
                    var nullable = field.IsRequired ? "NOT NULL" : "NULL";
                    columns.Add($"{QuoteIdentifier(field.FieldName + "Id")} uuid {nullable}");
                }
                else
                {
                    var colDef = GetColumnDefinition(field);
                    columns.Add(colDef);
                }
            }

            columns.Add("PRIMARY KEY (\"ID\")");

            var sql = $"CREATE TABLE {tableName} (\n    {string.Join(",\n    ", columns)}\n)";
            _logger?.LogInformation("Creating table: {TableName}", cc.ClassName);
            ExecuteNonQuery(conn, sql);

            // Add FK constraints for reference fields
            AddForeignKeyConstraints(conn, cc);
        }

        private void AddMissingColumns(NpgsqlConnection conn, CustomClass cc)
        {
            var existingColumns = GetExistingColumns(conn, cc.ClassName);
            var tableName = QuoteIdentifier(cc.ClassName);

            foreach (var field in cc.Fields.Where(f => !string.IsNullOrWhiteSpace(f.FieldName)))
            {
                if (IsReferenceField(field))
                {
                    var fkColName = PgName(field.FieldName + "Id");
                    if (!existingColumns.Contains(fkColName))
                    {
                        // DATA-003: a NOT NULL uuid column has no sensible default, and adding it NULL
                        // would leave rows EF cannot materialize into the non-nullable Guid property.
                        // Refuse on a populated table; the error aborts the deploy with a clear message.
                        if (field.IsRequired && RowCount(conn, cc.ClassName) is var rows && rows > 0)
                            throw new InvalidOperationException(
                                $"Cannot add required reference '{field.FieldName}' to '{cc.ClassName}' with {rows} existing row(s). " +
                                "Add it as optional, backfill the references, then make it required.");
                        var nullable = field.IsRequired ? "NOT NULL" : "NULL";
                        var sql = $"ALTER TABLE {tableName} ADD COLUMN {QuoteIdentifier(fkColName)} uuid {nullable}";
                        _logger?.LogInformation("Adding FK column: {TableName}.{ColumnName}", cc.ClassName, fkColName);
                        ExecuteNonQuery(conn, sql);
                    }
                }
                else
                {
                    if (!existingColumns.Contains(PgName(field.FieldName)))
                    {
                        var pgType = SupportedTypes.GetPostgresType(field.TypeName);
                        var nullable = field.IsRequired ? "NOT NULL" : "NULL";
                        var defaultVal = field.IsRequired ? $" DEFAULT {SupportedTypes.GetPostgresDefault(field.TypeName)}" : "";

                        var sql = $"ALTER TABLE {tableName} ADD COLUMN {QuoteIdentifier(field.FieldName)} {pgType} {nullable}{defaultVal}";
                        _logger?.LogInformation("Adding column: {TableName}.{ColumnName}", cc.ClassName, field.FieldName);
                        ExecuteNonQuery(conn, sql);
                    }
                }
            }

            // Add FK constraints for any new reference fields
            AddForeignKeyConstraints(conn, cc);
        }

        private string GetColumnDefinition(CustomField field)
        {
            var pgType = SupportedTypes.GetPostgresType(field.TypeName);
            var nullable = field.IsRequired ? "NOT NULL" : "NULL";
            var defaultVal = field.IsRequired ? $" DEFAULT {SupportedTypes.GetPostgresDefault(field.TypeName)}" : "";

            return $"{QuoteIdentifier(field.FieldName)} {pgType} {nullable}{defaultVal}";
        }

        private void AddForeignKeyConstraints(NpgsqlConnection conn, CustomClass cc)
        {
            var tableName = QuoteIdentifier(cc.ClassName);

            foreach (var field in cc.Fields.Where(f => IsReferenceField(f)))
            {
                var constraintName = PgName($"FK_{cc.ClassName}_{field.FieldName}");
                if (ConstraintExists(conn, cc.ClassName, constraintName))
                    continue;

                var refTableName = field.ReferencedClassName;
                if (!TableExists(conn, refTableName))
                    continue; // Target table doesn't exist yet; FK will be added on next sync

                var fkColName = QuoteIdentifier(field.FieldName + "Id");
                var sql = $"ALTER TABLE {tableName} ADD CONSTRAINT {QuoteIdentifier(constraintName)} " +
                          $"FOREIGN KEY ({fkColName}) REFERENCES {QuoteIdentifier(refTableName)} (\"ID\")";
                _logger?.LogInformation("Adding FK constraint: {Constraint}", constraintName);
                try
                {
                    ExecuteNonQuery(conn, sql);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning("FK constraint failed (non-fatal): {Error}", ex.Message);
                }
            }
        }

        // Scoped to the owning table: two long names can truncate to the same constraint name
        // (Codex review on DATA-006), and constraint names only need to be unique per table.
        private bool ConstraintExists(NpgsqlConnection conn, string tableName, string constraintName)
        {
            using var cmd = new NpgsqlCommand(
                "SELECT EXISTS (SELECT FROM information_schema.table_constraints WHERE constraint_name = @name AND table_name = @table AND constraint_schema = 'public')",
                conn);
            cmd.Parameters.AddWithValue("name", PgName(constraintName));
            cmd.Parameters.AddWithValue("table", PgName(tableName));
            return (bool)cmd.ExecuteScalar();
        }

        private static bool IsReferenceField(CustomField field)
        {
            return !string.IsNullOrWhiteSpace(field.ReferencedClassName)
                && (field.TypeName == "Reference" || string.IsNullOrWhiteSpace(field.TypeName));
        }

        private static long RowCount(NpgsqlConnection conn, string tableName)
        {
            using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {QuoteIdentifier(tableName)}", conn);
            return (long)cmd.ExecuteScalar();
        }

        private bool TableExists(NpgsqlConnection conn, string tableName)
        {
            using var cmd = new NpgsqlCommand(
                "SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @name)",
                conn);
            cmd.Parameters.AddWithValue("name", PgName(tableName));
            return (bool)cmd.ExecuteScalar();
        }

        private HashSet<string> GetExistingColumns(NpgsqlConnection conn, string tableName)
        {
            // ponytail: Ordinal (case-sensitive) — Postgres treats "Email" and "email" as
            // distinct columns (DATA-001). A case-insensitive check let a stale differently-
            // cased column silently satisfy the existence check for the real, exact-quoted
            // column name, so the correctly-cased ALTER TABLE ADD COLUMN never ran.
            var columns = new HashSet<string>(StringComparer.Ordinal);
            using var cmd = new NpgsqlCommand(
                "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @name",
                conn);
            cmd.Parameters.AddWithValue("name", PgName(tableName));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(0));
            }
            return columns;
        }

        private void ExecuteNonQuery(NpgsqlConnection conn, string sql)
        {
            _logger?.LogDebug("Executing DDL: {Sql}", sql);
            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// DATA-006: PostgreSQL truncates identifiers to 63 bytes (NAMEDATALEN-1). Existence checks
        /// and generated constraint names must use the truncated form or every sync re-creates.
        /// Names are ASCII by the validation regex, so chars == bytes.
        /// </summary>
        public static string PgName(string s) => s.Length > 63 ? s[..63] : s;

        private static string QuoteIdentifier(string name)
        {
            return $"\"{name.Replace("\"", "\"\"")}\"";
        }
    }
}
