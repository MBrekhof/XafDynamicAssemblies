using Microsoft.Extensions.Logging;
using Npgsql;
using XafDynamicAssemblies.Module.BusinessObjects;

namespace XafDynamicAssemblies.Module.Services
{
    /// <summary>
    /// Executes PostgreSQL DDL to create/alter tables for runtime entities.
    /// Never drops columns — only adds.
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

            foreach (var cc in classes)
            {
                SynchronizeTable(conn, cc);
            }
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
                    var fkColName = field.FieldName + "Id";
                    if (!existingColumns.Contains(fkColName, StringComparer.OrdinalIgnoreCase))
                    {
                        var nullable = field.IsRequired ? "NOT NULL" : "NULL";
                        var sql = $"ALTER TABLE {tableName} ADD COLUMN {QuoteIdentifier(fkColName)} uuid {nullable}";
                        _logger?.LogInformation("Adding FK column: {TableName}.{ColumnName}", cc.ClassName, fkColName);
                        ExecuteNonQuery(conn, sql);
                    }
                }
                else
                {
                    if (!existingColumns.Contains(field.FieldName, StringComparer.OrdinalIgnoreCase))
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
                var constraintName = $"FK_{cc.ClassName}_{field.FieldName}";
                if (ConstraintExists(conn, constraintName))
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

        private bool ConstraintExists(NpgsqlConnection conn, string constraintName)
        {
            using var cmd = new NpgsqlCommand(
                "SELECT EXISTS (SELECT FROM information_schema.table_constraints WHERE constraint_name = @name AND constraint_schema = 'public')",
                conn);
            cmd.Parameters.AddWithValue("name", constraintName);
            return (bool)cmd.ExecuteScalar();
        }

        private static bool IsReferenceField(CustomField field)
        {
            return !string.IsNullOrWhiteSpace(field.ReferencedClassName)
                && (field.TypeName == "Reference" || string.IsNullOrWhiteSpace(field.TypeName));
        }

        private bool TableExists(NpgsqlConnection conn, string tableName)
        {
            using var cmd = new NpgsqlCommand(
                "SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @name)",
                conn);
            cmd.Parameters.AddWithValue("name", tableName);
            return (bool)cmd.ExecuteScalar();
        }

        private HashSet<string> GetExistingColumns(NpgsqlConnection conn, string tableName)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = new NpgsqlCommand(
                "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @name",
                conn);
            cmd.Parameters.AddWithValue("name", tableName);
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

        private static string QuoteIdentifier(string name)
        {
            return $"\"{name.Replace("\"", "\"\"")}\"";
        }
    }
}
