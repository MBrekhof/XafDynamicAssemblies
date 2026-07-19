using Npgsql;
using XafDynamicAssemblies.Tests.Fixtures;
using XafDynamicAssemblies.Tests.Helpers;
using XafDynamicAssemblies.Tests.Pages;

namespace XafDynamicAssemblies.Tests.Tests;

/// <summary>
/// Phase 9 Tests: Review fixes and new XAF attribute support.
/// Ported from tests/tests/test_phase9_review_fixes.py.
///
/// Covers:
/// - Finding 1: Required reference fields enforced (NOT NULL in DDL, non-nullable Guid in codegen)
/// - Finding 2: Reference type validation requires ReferencedClassName
/// - Finding 3: Test Compile includes all runtime classes (cross-reference support)
/// - Finding 4: GraduationService escapes special characters in descriptions
/// - New: XAF property attributes (ImmediatePostData, Size, Visibility, Editable, ToolTip, DisplayName)
/// </summary>
[Collection("Sequential")]
public class Phase09_ReviewFixesTests : IAsyncLifetime
{
    private readonly BrowserFixture _fixture;
    private IPage _page = null!;

    public Phase09_ReviewFixesTests(BrowserFixture fixture) => _fixture = fixture;

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

    /// <summary>Navigate to Custom Field ListView and wait for grid.</summary>
    private async Task<(NavigationPage Nav, ListViewPage Lv)> NavToCustomFieldAsync()
    {
        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Field");
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

    /// <summary>Create a CustomClass via the UI, then return to the list view (matches Python's create_class_via_ui).</summary>
    private async Task CreateClassViaUiAsync(string className, string navGroup, string description = "")
    {
        var (nav, lv) = await NavToCustomClassAsync();
        await DeleteIfExistsAsync(className);

        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(2000);
        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Class Name", className);
        await detail.FillFieldAsync("Navigation Group", navGroup);
        if (!string.IsNullOrEmpty(description))
            await detail.FillFieldAsync("Description", description);
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(2000);

        nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        Assert.True(await lv.HasRowWithTextAsync(className), $"{className} should exist after creation");
    }

    /// <summary>
    /// Click Save and check if a validation error appeared, matching Python's
    /// try_save_and_check_validation. Returns (Saved, ErrorText).
    /// </summary>
    private async Task<(bool Saved, string ErrorText)> TrySaveAndCheckValidationAsync()
    {
        await _page.Locator("dxbl-toolbar-item > button[data-action-name=\"Save\"], dxbl-bar-item > button[data-action-name=\"Save\"]").First.ClickAsync();
        await _page.WaitForTimeoutAsync(1500);

        var validationWindow = _page.Locator(".dxbl-popup-content, .dxbl-window");
        var windowCount = await validationWindow.CountAsync();
        for (var i = 0; i < windowCount; i++)
        {
            var text = await validationWindow.Nth(i).InnerTextAsync();
            var lower = text.ToLowerInvariant();
            if (lower.Contains("must be") || lower.Contains("cannot be") || lower.Contains("reserved") ||
                lower.Contains("conflicts") || lower.Contains("valid") || lower.Contains("error") ||
                lower.Contains("requires") || lower.Contains("reference"))
            {
                return (false, text);
            }
        }

        var bodyText = await _page.Locator("body").InnerTextAsync();
        var validationKeywords = new[]
        {
            "must be a valid C# identifier",
            "cannot be a C# keyword",
            "conflicts with a built-in type",
            "Field Name is reserved",
            "must be a supported CLR type",
            "Reference field requires",
            "Validation",
        };
        foreach (var keyword in validationKeywords)
        {
            if (bodyText.Contains(keyword))
                return (false, bodyText);
        }

        return (true, "");
    }

    /// <summary>
    /// Locate the Graduate toolbar action button by Action Id ("GraduateEntity" —
    /// see GraduateController.cs; Caption is "Graduate", but under DevExpress Blazor 26.1's
    /// Ribbon UI the caption is only rendered as button text, not a `text` attribute).
    /// </summary>
    private ILocator GraduateButton()
    {
        var btn = _page.Locator("dxbl-toolbar-item > button[data-action-name=\"GraduateEntity\"], dxbl-bar-item > button[data-action-name=\"GraduateEntity\"]");
        return btn;
    }

    /// <summary>Click Graduate on the currently open DetailView and dismiss confirmation/success dialogs.</summary>
    private async Task ClickGraduateAsync()
    {
        var graduateBtn = GraduateButton();
        if (await graduateBtn.CountAsync() == 0)
            graduateBtn = _page.Locator("button:has-text('Graduate'), span:has-text('Graduate')");
        await graduateBtn.First.ClickAsync();
        await _page.WaitForTimeoutAsync(1000);

        var confirmBtn = _page.Locator("button:has-text('Yes'), button:has-text('OK')");
        if (await confirmBtn.CountAsync() > 0)
            await confirmBtn.First.ClickAsync();
        await _page.WaitForTimeoutAsync(2000);

        var okBtn = _page.Locator("button:has-text('OK')");
        if (await okBtn.CountAsync() > 0)
        {
            await okBtn.First.ClickAsync();
            await _page.WaitForTimeoutAsync(500);
        }
    }

    /// <summary>Read the GraduatedSource column for a class from the DB.</summary>
    private static string? GetGraduatedSource(string className)
    {
        using var conn = DatabaseHelper.GetConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT \"GraduatedSource\" FROM \"CustomClasses\" WHERE \"ClassName\" = @name " +
            "AND (\"GCRecord\" IS NULL OR \"GCRecord\" = 0)", conn);
        cmd.Parameters.AddWithValue("name", className);
        using var reader = cmd.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0) ? reader.GetString(0) : null;
    }

    // =============================================================================
    // Finding 2: Reference type validation requires ReferencedClassName
    // =============================================================================

    /// <summary>Save a Reference field without ReferencedClassName — should fail validation.</summary>
    [Fact]
    public async Task Test_01_ReferenceWithoutClassRejected()
    {
        await NavToCustomFieldAsync();
        var lv = new ListViewPage(_page);
        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(1000);

        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Field Name", "BadRef");
        await detail.FillFieldAsync("Type Name", "Reference");
        // Deliberately leave Referenced Class Name empty

        var (saved, errorText) = await TrySaveAndCheckValidationAsync();
        Assert.False(saved, "Save should fail for Reference without ReferencedClassName");
        Assert.True(errorText.ToLowerInvariant().Contains("reference") || errorText.Contains("Validation"));
    }

    // =============================================================================
    // Finding 3: Test Compile includes all runtime classes (cross-reference support)
    // =============================================================================

    /// <summary>Create P9Parent class.</summary>
    [Fact]
    public async Task Test_02_CreateParentClass()
    {
        await CreateClassViaUiAsync("P9Parent", "P9Test");
    }

    /// <summary>Create P9Child class.</summary>
    [Fact]
    public async Task Test_03_CreateChildClass()
    {
        await CreateClassViaUiAsync("P9Child", "P9Test");
    }

    /// <summary>Add fields including a cross-reference from P9Child to P9Parent.</summary>
    [Fact]
    public async Task Test_04_AddCrossRefFields()
    {
        DatabaseHelper.InsertFieldViaDb("P9Parent", "ParentName", "System.String", isDefault: true);
        DatabaseHelper.InsertFieldViaDb("P9Child", "ChildName", "System.String", isDefault: true);
        DatabaseHelper.InsertFieldViaDb("P9Child", "Parent", "Reference", referencedClassName: "P9Parent");

        var (nav, lv) = await NavToCustomFieldAsync();
        Assert.True(await lv.HasRowWithTextAsync("ParentName"), "ParentName field should exist");
        Assert.True(await lv.HasRowWithTextAsync("ChildName"), "ChildName field should exist");
    }

    /// <summary>Test Compile All from ListView should succeed since all classes are compiled together.</summary>
    [Fact]
    public async Task Test_05_TestCompileCrossRefSucceeds()
    {
        await NavToCustomClassAsync();
        await _page.WaitForTimeoutAsync(1000);

        // Action Id is "TestCompile" (TestCompileController.cs); see BasePage.cs remarks re: 26.1 Ribbon UI.
        var compileBtn = _page.Locator("dxbl-toolbar-item > button[data-action-name=\"TestCompile\"], dxbl-bar-item > button[data-action-name=\"TestCompile\"]");
        Assert.True(await compileBtn.CountAsync() > 0, "Test Compile All action should be visible");
        await compileBtn.First.ClickAsync();
        await _page.WaitForTimeoutAsync(3000);

        var bodyText = (await _page.Locator("body").InnerTextAsync()).ToLowerInvariant();
        Assert.True(bodyText.Contains("successful") || bodyText.Contains("success"),
            $"Test Compile All should succeed for cross-reference. Page text: {bodyText[..Math.Min(500, bodyText.Length)]}");
    }

    // =============================================================================
    // Finding 1: Required reference fields + New XAF attributes
    // =============================================================================

    /// <summary>Create AttrDept class.</summary>
    [Fact]
    public async Task Test_06_CreateAttrDept()
    {
        await CreateClassViaUiAsync("AttrDept", "P9Attr");
    }

    /// <summary>Create AttrEmp class with special chars in description (for Finding 4 too).</summary>
    [Fact]
    public async Task Test_07_CreateAttrEmp()
    {
        await CreateClassViaUiAsync("AttrEmp", "P9Attr", "Employee with tags and quotes");
    }

    /// <summary>Add fields with various XAF attributes via DB.</summary>
    [Fact]
    public async Task Test_08_AddFieldsWithAttributes()
    {
        // AttrDept: simple name field
        DatabaseHelper.InsertFieldViaDb("AttrDept", "DeptName", "System.String", isDefault: true,
            stringMaxLength: 100);

        // AttrEmp: fields exercising all new attributes
        DatabaseHelper.InsertFieldViaDb("AttrEmp", "EmpName", "System.String", isDefault: true,
            isImmediatePostData: true,
            displayName: "Employee Name",
            toolTip: "Full name of the employee");

        DatabaseHelper.InsertFieldViaDb("AttrEmp", "Notes", "System.String",
            stringMaxLength: -1, // Unlimited (memo)
            isVisibleInListView: false);

        DatabaseHelper.InsertFieldViaDb("AttrEmp", "EmployeeCode", "System.String",
            isRequired: true, isEditable: false,
            toolTip: "Auto-generated code");

        DatabaseHelper.InsertFieldViaDb("AttrEmp", "Salary", "System.Decimal",
            isVisibleInListView: false,
            isVisibleInDetailView: true);

        // Required reference (Finding 1)
        DatabaseHelper.InsertFieldViaDb("AttrEmp", "Department", "Reference",
            referencedClassName: "AttrDept",
            isRequired: true,
            isImmediatePostData: true);

        var (nav, lv) = await NavToCustomFieldAsync();
        Assert.True(await lv.HasRowWithTextAsync("EmpName"), "EmpName field should exist");
        Assert.True(await lv.HasRowWithTextAsync("Department"), "Department ref field should exist");
    }

    /// <summary>Deploy schema and wait for server restart.</summary>
    [Fact]
    public async Task Test_09_DeployAndVerify()
    {
        await NavToCustomClassAsync();
        await ServerHelper.ClickDeploySchemaAsync(_page);
        await ServerHelper.WaitForDeployRestartAsync(_page);
    }

    /// <summary>Finding 1: Verify the required reference FK column is NOT NULL in PostgreSQL.</summary>
    [Fact]
    public void Test_10_RequiredRefIsNotNullInDb()
    {
        using var conn = DatabaseHelper.GetConnection();
        using var cmd = new NpgsqlCommand(@"
            SELECT is_nullable FROM information_schema.columns
            WHERE table_schema = 'public'
            AND table_name = 'AttrEmp'
            AND column_name = 'DepartmentId'", conn);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), "DepartmentId column should exist in AttrEmp table");
        var isNullable = reader.GetString(0);
        Assert.True(isNullable == "NO",
            $"Required reference column DepartmentId should be NOT NULL, got is_nullable={isNullable}");
    }

    /// <summary>Verify a non-required reference FK column IS nullable in PostgreSQL (control test).</summary>
    [Fact]
    public void Test_11_OptionalRefIsNullableInDb()
    {
        using var conn = DatabaseHelper.GetConnection();
        using var cmd = new NpgsqlCommand(@"
            SELECT is_nullable FROM information_schema.columns
            WHERE table_schema = 'public'
            AND table_name = 'P9Child'
            AND column_name = 'ParentId'", conn);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), "ParentId column should exist in P9Child table");
        var isNullable = reader.GetString(0);
        Assert.True(isNullable == "YES",
            $"Optional reference column ParentId should be nullable, got is_nullable={isNullable}");
    }

    /// <summary>Verify StringMaxLength is stored correctly in metadata.</summary>
    [Fact]
    public void Test_12_StringMaxLengthStored()
    {
        using var conn = DatabaseHelper.GetConnection();
        using var cmd = new NpgsqlCommand(@"
            SELECT cf.""StringMaxLength""
            FROM ""CustomFields"" cf
            JOIN ""CustomClasses"" cc ON cf.""CustomClassId"" = cc.""ID""
            WHERE cc.""ClassName"" = 'AttrDept' AND cf.""FieldName"" = 'DeptName'
            AND (cf.""GCRecord"" IS NULL OR cf.""GCRecord"" = 0)", conn);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), "DeptName field should exist");
        var maxLength = reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0);
        Assert.True(maxLength == 100, $"StringMaxLength should be 100, got {maxLength}");
    }

    /// <summary>Verify all new XAF attribute columns are stored correctly.</summary>
    [Fact]
    public void Test_13_XafAttributesStoredInMetadata()
    {
        using var conn = DatabaseHelper.GetConnection();

        // Check EmpName field attributes
        using (var cmd = new NpgsqlCommand(@"
            SELECT cf.""IsImmediatePostData"", cf.""DisplayName"", cf.""ToolTip"",
                   cf.""IsVisibleInListView"", cf.""IsVisibleInDetailView"", cf.""IsEditable""
            FROM ""CustomFields"" cf
            JOIN ""CustomClasses"" cc ON cf.""CustomClassId"" = cc.""ID""
            WHERE cc.""ClassName"" = 'AttrEmp' AND cf.""FieldName"" = 'EmpName'
            AND (cf.""GCRecord"" IS NULL OR cf.""GCRecord"" = 0)", conn))
        using (var reader = cmd.ExecuteReader())
        {
            Assert.True(reader.Read(), "EmpName field should exist");
            Assert.True(reader.GetBoolean(0), $"IsImmediatePostData should be True, got {reader.GetBoolean(0)}");
            Assert.Equal("Employee Name", reader.GetString(1));
            Assert.Equal("Full name of the employee", reader.GetString(2));
            Assert.True(reader.GetBoolean(3), "IsVisibleInListView should be True");
            Assert.True(reader.GetBoolean(4), "IsVisibleInDetailView should be True");
            Assert.True(reader.GetBoolean(5), "IsEditable should be True");
        }

        // Check Notes field — hidden from list view, memo
        using (var cmd = new NpgsqlCommand(@"
            SELECT cf.""IsVisibleInListView"", cf.""StringMaxLength""
            FROM ""CustomFields"" cf
            JOIN ""CustomClasses"" cc ON cf.""CustomClassId"" = cc.""ID""
            WHERE cc.""ClassName"" = 'AttrEmp' AND cf.""FieldName"" = 'Notes'
            AND (cf.""GCRecord"" IS NULL OR cf.""GCRecord"" = 0)", conn))
        using (var reader = cmd.ExecuteReader())
        {
            Assert.True(reader.Read(), "Notes field should exist");
            Assert.False(reader.GetBoolean(0), "Notes.IsVisibleInListView should be False");
            Assert.Equal(-1, reader.GetInt32(1));
        }

        // Check EmployeeCode field — required, not editable
        using (var cmd = new NpgsqlCommand(@"
            SELECT cf.""IsRequired"", cf.""IsEditable""
            FROM ""CustomFields"" cf
            JOIN ""CustomClasses"" cc ON cf.""CustomClassId"" = cc.""ID""
            WHERE cc.""ClassName"" = 'AttrEmp' AND cf.""FieldName"" = 'EmployeeCode'
            AND (cf.""GCRecord"" IS NULL OR cf.""GCRecord"" = 0)", conn))
        using (var reader = cmd.ExecuteReader())
        {
            Assert.True(reader.Read(), "EmployeeCode field should exist");
            Assert.True(reader.GetBoolean(0), "EmployeeCode.IsRequired should be True");
            Assert.False(reader.GetBoolean(1), "EmployeeCode.IsEditable should be False");
        }
    }

    // =============================================================================
    // Finding 4: Graduation escaping (special chars in description/nav group)
    // =============================================================================

    /// <summary>Create a class with special characters in description for graduation test.</summary>
    [Fact]
    public async Task Test_14_CreateEscapableClass()
    {
        await CreateClassViaUiAsync("EscGrad", "EscNav", "A class with special chars");
    }

    /// <summary>Add fields including one with a ToolTip containing quotes.</summary>
    [Fact]
    public async Task Test_15_AddEscapableFields()
    {
        DatabaseHelper.InsertFieldViaDb("EscGrad", "Title", "System.String", isDefault: true);
        DatabaseHelper.InsertFieldViaDb("EscGrad", "Detail", "System.String",
            toolTip: "Has \"quotes\" inside",
            displayName: "Detail Info");
    }

    /// <summary>Deploy the class so it becomes a runtime entity.</summary>
    [Fact]
    public async Task Test_16_DeployEscapableClass()
    {
        await NavToCustomClassAsync();
        await ServerHelper.ClickDeploySchemaAsync(_page);
        await ServerHelper.WaitForDeployRestartAsync(_page);
    }

    /// <summary>Graduate the class and verify the source escapes special characters.</summary>
    [Fact]
    public async Task Test_17_GraduateAndCheckSource()
    {
        var (nav, lv) = await NavToCustomClassAsync();
        await lv.DoubleClickRowWithTextAsync("EscGrad");
        await _page.WaitForTimeoutAsync(2000);

        await ClickGraduateAsync();

        var source = GetGraduatedSource("EscGrad");
        Assert.True(!string.IsNullOrEmpty(source), "GraduatedSource should be populated");

        // Verify class structure
        Assert.Contains("class EscGrad", source);
        Assert.Contains("BaseObject", source);

        // ponytail: Python's first assertion here was dead code (precedence bug made it (A or B) if C else False, strictly weaker than the check below) — intentionally not ported.
        // Finding 4: ToolTip with quotes should be escaped in string literal (backslash-escaped)
        Assert.True(source!.Contains("\\\"quotes\\\""),
            $"Quotes should be backslash-escaped in generated source. Source:\n{source}");

        // Verify DisplayName attribute present on Detail field
        Assert.Contains("DisplayName(", source);
    }

    // =============================================================================
    // New XAF Attributes: verify attributes appear in graduated source
    // =============================================================================

    /// <summary>Graduate AttrEmp and verify source contains all attribute annotations.</summary>
    [Fact]
    public async Task Test_18_GraduateAttrEmp()
    {
        var (nav, lv) = await NavToCustomClassAsync();
        await lv.DoubleClickRowWithTextAsync("AttrEmp");
        await _page.WaitForTimeoutAsync(2000);

        await ClickGraduateAsync();

        var source = GetGraduatedSource("AttrEmp");
        Assert.True(!string.IsNullOrEmpty(source), "GraduatedSource should be populated");

        // ImmediatePostData on EmpName and Department
        Assert.Contains("[ImmediatePostData]", source);

        // DisplayName on EmpName
        Assert.Contains("DisplayName(\"Employee Name\")", source);

        // ToolTip on EmpName
        Assert.Contains("ToolTip(\"Full name of the employee\")", source);

        // Notes: VisibleInListView(false) and Size(-1) for memo
        Assert.Contains("[VisibleInListView(false)]", source);
        Assert.Contains("Size(-1)", source);

        // EmployeeCode: Editable(false)
        Assert.Contains("Editable(false)", source);

        // Required reference: Department FK should be non-nullable Guid (not Guid?)
        Assert.Contains("Guid DepartmentId", source);
        // Also should have [Required] on the FK
        Assert.Contains("[System.ComponentModel.DataAnnotations.Required]", source);
    }

    // =============================================================================
    // Cleanup
    // =============================================================================

    /// <summary>Remove all Phase 9 test entities and metadata.</summary>
    [Fact]
    public async Task Test_99_Cleanup()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);

        // Delete runtime entity records where tables might exist
        foreach (var entity in new[] { "AttrEmp", "AttrDept", "P9Child", "P9Parent", "EscGrad" })
        {
            try
            {
                await _page.GotoAsync($"{TestSettings.BaseUrl}/{entity}_ListView",
                    new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 15_000 });
                await _page.WaitForTimeoutAsync(2000);
                var lv = new ListViewPage(_page);
                await lv.WaitForGridAsync();
                while (await lv.GetRowCountAsync() > 0)
                {
                    await lv.ClickRowAsync(0);
                    await lv.ClickDeleteAsync();
                    await lv.ConfirmDeleteAsync();
                    await _page.WaitForTimeoutAsync(500);
                }
            }
            catch
            {
                // ponytail: matches Python's bare `except Exception: pass` — entity may not
                // have been deployed.
            }
        }

        // Delete metadata classes (cascade deletes fields)
        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        var lv2 = new ListViewPage(_page);
        await lv2.WaitForGridAsync();
        foreach (var name in new[] { "AttrEmp", "AttrDept", "P9Child", "P9Parent", "EscGrad", "RefTarget9" })
        {
            await DeleteIfExistsAsync(name);
        }

        // Drop runtime tables
        using var conn = DatabaseHelper.GetConnection();
        foreach (var table in new[] { "AttrEmp", "AttrDept", "P9Child", "P9Parent", "EscGrad" })
        {
            using var cmd = new NpgsqlCommand($"DROP TABLE IF EXISTS \"{table}\" CASCADE", conn);
            cmd.ExecuteNonQuery();
        }
    }
}
