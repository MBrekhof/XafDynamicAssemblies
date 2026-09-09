# TODO — XafDynamicAssemblies

Source: Codex full-repo review 2026-09-08 (27 findings), each verified against the source and DX 26.1 installed sources before carding. Severity in the body is mine, Codex's original is quoted.

## P2: Medium

#### DATA-007: Startup guard drops metadata fields whose TypeName or FK target disagrees with the live column (ID: 1598)

**Source:** kashiash/XafXPODynAssem-odczepiony commits 1b8a565 + 549e29f (2026-08-30), `Module/Validation/FieldTypeChangeGuard.cs` — a detached copy of our XPO sibling. Port the startup sanitizer and the pure mismatch function; skip their "never delete a field" rule and delete-guard controller.

**Problem here.** `SchemaSynchronizer` is add-only (and DATA-002 makes the XAF updater add-only), so a `TypeName` change on a deployed field leaves the SQL column as is. Roslyn generates a CLR property whose type disagrees with the column, and every ListView/OData query on that entity fails after restart with no UI path to see why. A Reference retargeted to another class keeps its `uuid` column and old FK the same way.

**Scope:**
- Pure `FindDatabaseMismatch(className, fieldName, typeName, referencedClassName, columnDataType, fkTargetTable, Func<bool> hasData)` → `null` or reason. TypeName → accepted `information_schema.data_type` set (our mapping in CLAUDE.md); unknown on either side = compatible. Reference: column must be `uuid`; an existing single-column FK must target `ReferencedClassName`; `uuid` with data and no FK is a mismatch too (adding the FK would fail 23503). Absent column → `null`.
- Startup sanitizer in `QueryMetadata` (Module.cs) before Roslyn: two set-based queries (`information_schema.columns` for all runtime tables; `pg_constraint` + `unnest(conkey)` for single-column FKs), remove mismatched fields from the list, log `[SchemaGuard]` per field, keep a static `SkippedFieldWarnings` (reset in `ResetForRestart`) and surface it in `validate_schema` output.

**Test:** unit tests on the pure function (compatible, type mismatch, FK retarget, uuid+data+no-FK, absent column); one E2E that flips a deployed field's TypeName via SQL, restarts, and asserts the ListView loads with the field absent and the warning logged.

(Completed work: see `docs/DONE.md`. Future ideas: `BACKBURNER.md`.)
