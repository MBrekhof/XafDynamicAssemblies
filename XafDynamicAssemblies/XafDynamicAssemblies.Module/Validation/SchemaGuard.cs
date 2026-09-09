using DevExpress.Persistent.Base;
using Npgsql;
using XafDynamicAssemblies.Module.BusinessObjects;
using XafDynamicAssemblies.Module.Services;

namespace XafDynamicAssemblies.Module.Validation
{
    /// <summary>
    /// DATA-007: startup guard. SchemaSynchronizer and the XAF updater are add-only, so a
    /// TypeName change (or a Reference retarget) on a deployed field leaves the SQL column as it
    /// was; Roslyn would then generate a CLR property the column cannot materialize and every
    /// query on the entity fails after restart. The guard drops such fields from the metadata
    /// list before compilation (class and database untouched) and records why, so reads recover
    /// and validate_schema shows the reason. Ported from the XPO sibling's FieldTypeChangeGuard.
    /// Limitation: a skipped required reference leaves its NOT NULL column without a default, so
    /// reads recover while inserts still fail until the metadata is fixed.
    /// </summary>
    public static class SchemaGuard
    {
        /// <summary>information_schema.data_type values a TypeName can be read from.</summary>
        private static readonly Dictionary<string, string[]> AcceptedPgTypes = new(StringComparer.Ordinal)
        {
            ["System.String"] = new[] { "text", "character varying", "character" },
            ["System.Int32"] = new[] { "integer" },
            ["System.Int64"] = new[] { "bigint" },
            ["System.Decimal"] = new[] { "numeric" },
            ["System.Double"] = new[] { "double precision" },
            ["System.Single"] = new[] { "real" },
            ["System.Boolean"] = new[] { "boolean" },
            ["System.DateTime"] = new[] { "timestamp without time zone", "timestamp with time zone" },
            ["System.Guid"] = new[] { "uuid" },
            ["System.Byte[]"] = new[] { "bytea" },
            ["Reference"] = new[] { "uuid" },
        };

        private static readonly HashSet<string> KnownPgTypes =
            new(AcceptedPgTypes.Values.SelectMany(v => v), StringComparer.OrdinalIgnoreCase);

        /// <summary>Fields skipped by the last metadata query ("Class.Field: reason"); empty when none.</summary>
        public static IReadOnlyList<string> SkippedFieldWarnings { get; private set; } = Array.Empty<string>();

        public static void Reset() => SkippedFieldWarnings = Array.Empty<string>();

        private static bool IsReference(CustomField f) =>
            !string.IsNullOrWhiteSpace(f.ReferencedClassName)
            && (f.TypeName == "Reference" || string.IsNullOrWhiteSpace(f.TypeName));

        /// <summary>The SQL column a field maps to (references get the "Id" companion).</summary>
        public static string ColumnName(CustomField f) =>
            SchemaSynchronizer.PgName(IsReference(f) ? f.FieldName + "Id" : f.FieldName);

        /// <summary>
        /// Pure check. Returns null when the field can be materialized from the column, otherwise
        /// the reason. Absent column (null data type) and unknown types on either side are
        /// compatible: the synchronizer adds missing columns, and the guard only rejects what it
        /// knows to be wrong.
        /// </summary>
        /// <param name="fkTargetTables">target tables of single-column FKs on the column (empty when none)</param>
        /// <param name="targetIsRuntimeTable">
        /// true when ReferencedClassName is a runtime class, whose table name equals the class name.
        /// A compiled target's table is whatever the DbContext maps (CustomClass -> "CustomClasses"),
        /// which the guard cannot resolve, so FK retargets are only judged for runtime targets.
        /// </param>
        /// <param name="hasDanglingReferences">
        /// evaluated only for a Reference without an FK: true when the column holds a non-NULL id
        /// that does not exist in the target table, i.e. the ADD CONSTRAINT would fail with 23503
        /// </param>
        public static string FindMismatch(CustomField field, string columnDataType,
            IReadOnlyCollection<string> fkTargetTables, bool targetIsRuntimeTable, Func<bool> hasDanglingReferences)
        {
            if (columnDataType == null || !KnownPgTypes.Contains(columnDataType)) return null;
            var typeName = IsReference(field) ? "Reference" : field.TypeName;
            if (typeName == null || !AcceptedPgTypes.TryGetValue(typeName, out var accepted)) return null;

            if (!accepted.Contains(columnDataType, StringComparer.OrdinalIgnoreCase))
                return $"metadata type {typeName} but column is {columnDataType}";

            if (!IsReference(field)) return null;

            var target = field.ReferencedClassName;
            var wrong = targetIsRuntimeTable
                ? fkTargetTables?.FirstOrDefault(t => !string.Equals(t, SchemaSynchronizer.PgName(target), StringComparison.Ordinal))
                : null;
            if (wrong != null)
                return $"references {target} but the FK constraint targets {wrong}";

            if ((fkTargetTables == null || fkTargetTables.Count == 0) && hasDanglingReferences())
                return $"references {target} without an FK constraint and holds ids that do not exist in {target} (ADD CONSTRAINT would fail)";

            return null;
        }

        /// <summary>
        /// Removes mismatched fields from <paramref name="classes"/> (in memory only), logs each as
        /// [SchemaGuard] and publishes the reasons in <see cref="SkippedFieldWarnings"/>.
        /// Never throws: a guard must not take startup down.
        /// </summary>
        public static void Sanitize(NpgsqlConnection conn, List<CustomClass> classes)
        {
            var warnings = new List<string>();
            try
            {
                var tableNames = classes.Select(c => SchemaSynchronizer.PgName(c.ClassName)).Distinct().ToArray();
                var referenced = classes.SelectMany(c => c.Fields).Where(IsReference)
                    .Select(f => SchemaSynchronizer.PgName(f.ReferencedClassName)).Distinct().ToArray();

                var columnTypes = LoadColumnTypes(conn, tableNames);
                var fks = LoadSingleColumnForeignKeys(conn, tableNames);
                var existingTables = LoadExistingTables(conn, tableNames.Concat(referenced).Distinct().ToArray());

                foreach (var cc in classes)
                {
                    var table = SchemaSynchronizer.PgName(cc.ClassName);
                    foreach (var field in cc.Fields.Where(f => !string.IsNullOrWhiteSpace(f.FieldName)).ToList())
                    {
                        string reason;
                        try
                        {
                            var column = ColumnName(field);
                            columnTypes.TryGetValue((table, column), out var dataType);
                            fks.TryGetValue((table, column), out var targets);
                            var isRuntimeTarget = IsReference(field) && tableNames.Contains(SchemaSynchronizer.PgName(field.ReferencedClassName), StringComparer.Ordinal);
                            reason = FindMismatch(field, dataType, (IReadOnlyCollection<string>)targets ?? Array.Empty<string>(), isRuntimeTarget, () =>
                            {
                                var targetTable = SchemaSynchronizer.PgName(field.ReferencedClassName);
                                // Missing target: the synchronizer adds the FK later; nothing to probe.
                                return existingTables.Contains(targetTable) && HasDanglingReferences(conn, table, column, targetTable);
                            });
                        }
                        catch (Exception ex)
                        {
                            // Probe failures are per field; log and keep the field.
                            Tracing.Tracer.LogError($"[SchemaGuard] probe failed for {cc.ClassName}.{field.FieldName}: {ex.Message}");
                            continue;
                        }

                        if (reason == null) continue;
                        cc.Fields.Remove(field);
                        var warning = $"{cc.ClassName}.{field.FieldName}: {reason}";
                        warnings.Add(warning);
                        Tracing.Tracer.LogWarning($"[SchemaGuard] skipped {warning}");
                    }
                }
            }
            catch (Exception ex)
            {
                Tracing.Tracer.LogError($"[SchemaGuard] sanitize failed (metadata used as is): {ex.Message}");
            }
            SkippedFieldWarnings = warnings;
        }

        private static Dictionary<(string Table, string Column), string> LoadColumnTypes(NpgsqlConnection conn, string[] tables)
        {
            var result = new Dictionary<(string, string), string>();
            if (tables.Length == 0) return result;
            using var cmd = new NpgsqlCommand(
                "SELECT table_name, column_name, data_type FROM information_schema.columns " +
                "WHERE table_schema = 'public' AND table_name = ANY(@tables)", conn);
            cmd.Parameters.AddWithValue("tables", tables);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
            return result;
        }

        private static Dictionary<(string Table, string Column), List<string>> LoadSingleColumnForeignKeys(NpgsqlConnection conn, string[] tables)
        {
            var result = new Dictionary<(string, string), List<string>>();
            if (tables.Length == 0) return result;
            using var cmd = new NpgsqlCommand(@"
                SELECT src.relname, a.attname, tgt.relname
                FROM pg_constraint c
                JOIN pg_class src ON src.oid = c.conrelid
                JOIN pg_namespace n ON n.oid = src.relnamespace
                JOIN pg_class tgt ON tgt.oid = c.confrelid
                JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = c.conkey[1]
                WHERE c.contype = 'f' AND array_length(c.conkey, 1) = 1
                  AND n.nspname = 'public' AND src.relname = ANY(@tables)", conn);
            cmd.Parameters.AddWithValue("tables", tables);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!result.TryGetValue(key, out var list)) result[key] = list = new List<string>();
                list.Add(reader.GetString(2));
            }
            return result;
        }

        private static HashSet<string> LoadExistingTables(NpgsqlConnection conn, string[] tables)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (tables.Length == 0) return result;
            using var cmd = new NpgsqlCommand(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_name = ANY(@tables)", conn);
            cmd.Parameters.AddWithValue("tables", tables);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(reader.GetString(0));
            return result;
        }

        private static bool HasDanglingReferences(NpgsqlConnection conn, string table, string column, string targetTable)
        {
            static string Q(string n) => "\"" + n.Replace("\"", "\"\"") + "\"";
            // Soft-deleted rows included on purpose: the physical FK constraint checks them too.
            using var cmd = new NpgsqlCommand(
                $"SELECT EXISTS (SELECT 1 FROM {Q(table)} t WHERE t.{Q(column)} IS NOT NULL " +
                $"AND NOT EXISTS (SELECT 1 FROM {Q(targetTable)} r WHERE r.\"ID\" = t.{Q(column)}))", conn);
            return (bool)cmd.ExecuteScalar();
        }
    }
}
