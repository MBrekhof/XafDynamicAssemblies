using XafDynamicAssemblies.Tests.Fixtures;
using XafDynamicAssemblies.Tests.Pages;

namespace XafDynamicAssemblies.Tests.Tests;

/// <summary>
/// Phase 3 Tests: Validation rules, TypeName dropdown, and Test Compile action.
/// Ported from tests/tests/test_phase3_validation.py.
///
/// Verifies that:
/// - Invalid class names are rejected (digits, keywords, reserved names)
/// - Invalid field names are rejected (reserved names like Id, GCRecord)
/// - TypeName dropdown shows supported types
/// - Test Compile All action (a ListView action, always enabled) validates Roslyn
///   compilation from the UI
/// </summary>
[Collection("Sequential")]
public class Phase03_ValidationTests : IAsyncLifetime
{
    private readonly BrowserFixture _fixture;
    private IPage _page = null!;

    public Phase03_ValidationTests(BrowserFixture fixture) => _fixture = fixture;

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

    /// <summary>
    /// Click Save and check if a validation error appeared.
    /// Returns (Saved, ErrorText): Saved=true means save succeeded (no validation error);
    /// Saved=false means validation blocked the save, ErrorText has the message.
    /// </summary>
    // ponytail: `timeout` is unused in the body — mirrors Python's try_save_and_check_validation,
    // which takes the same default-3000 parameter and never references it.
    private async Task<(bool Saved, string ErrorText)> TrySaveAndCheckValidationAsync(int timeout = 3000)
    {
        await _page.Locator("dxbl-toolbar-item > button[data-action-name=\"Save\"], dxbl-bar-item > button[data-action-name=\"Save\"]").First.ClickAsync();
        await _page.WaitForTimeoutAsync(1500);

        // XAF Blazor shows validation errors in a popup/window with error details.
        // Check for validation error indicators:

        // 1. Check for XAF validation result window (contains error messages)
        var validationWindow = _page.Locator(".dxbl-popup-content, .dxbl-window");
        var windowCount = await validationWindow.CountAsync();
        for (var i = 0; i < windowCount; i++)
        {
            var text = await validationWindow.Nth(i).InnerTextAsync();
            var lower = text.ToLowerInvariant();
            if (lower.Contains("must be") || lower.Contains("cannot be") || lower.Contains("reserved") ||
                lower.Contains("conflicts") || lower.Contains("valid") || lower.Contains("error"))
            {
                return (false, text);
            }
        }

        // 2. Check for any visible popup with validation-related text
        var bodyText = await _page.Locator("body").InnerTextAsync();
        var validationKeywords = new[]
        {
            "must be a valid C# identifier",
            "cannot be a C# keyword",
            "conflicts with a built-in type",
            "Field Name is reserved",
            "must be a supported CLR type",
            "Validation",
        };
        foreach (var keyword in validationKeywords)
        {
            if (bodyText.Contains(keyword))
                return (false, bodyText);
        }

        // 3. Check if we're still on the detail view (save didn't navigate away)
        // If a .dxbl-fl-ctrl is still visible, we might still be on detail view
        // But this alone doesn't confirm validation error

        return (true, "");
    }

    // --- TestCustomClassValidation ---

    /// <summary>Verify class name starting with digit is rejected.</summary>
    [Fact]
    public async Task Test_01_InvalidClassNameDigits()
    {
        await NavToCustomClassAsync();
        var lv = new ListViewPage(_page);
        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(1000);

        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Class Name", "123Invalid");
        var (saved, errorText) = await TrySaveAndCheckValidationAsync();

        Assert.False(saved, "Save should fail for class name starting with digit");
        Assert.True(errorText.Contains("valid C# identifier") || errorText.Contains("Validation"));
    }

    /// <summary>Verify C# keyword as class name is rejected.</summary>
    [Fact]
    public async Task Test_02_CSharpKeywordRejected()
    {
        await NavToCustomClassAsync();
        var lv = new ListViewPage(_page);
        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(1000);

        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Class Name", "class");
        var (saved, errorText) = await TrySaveAndCheckValidationAsync();

        Assert.False(saved, "Save should fail for C# keyword class name");
        Assert.True(errorText.ToLowerInvariant().Contains("keyword") || errorText.Contains("Validation"));
    }

    /// <summary>Verify reserved type name (BaseObject) is rejected.</summary>
    [Fact]
    public async Task Test_03_ReservedTypeRejected()
    {
        await NavToCustomClassAsync();
        var lv = new ListViewPage(_page);
        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(1000);

        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Class Name", "BaseObject");
        var (saved, errorText) = await TrySaveAndCheckValidationAsync();

        Assert.False(saved, "Save should fail for reserved type name");
        var lower = errorText.ToLowerInvariant();
        Assert.True(lower.Contains("conflicts") || lower.Contains("built-in") || errorText.Contains("Validation"));
    }

    /// <summary>Verify a valid class name is accepted.</summary>
    [Fact]
    public async Task Test_04_ValidClassNameAccepted()
    {
        var (nav, lv) = await NavToCustomClassAsync();
        await DeleteIfExistsAsync("ValidTestClass");

        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(1000);

        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Class Name", "ValidTestClass");
        await detail.FillFieldAsync("Navigation Group", "Test");
        var (saved, errorText) = await TrySaveAndCheckValidationAsync();

        Assert.True(saved, $"Save should succeed for valid class name. Error: {errorText}");

        // Navigate back and verify
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        await lv.WaitForGridAsync();
        await _page.WaitForTimeoutAsync(1000);
        Assert.True(await lv.HasRowWithTextAsync("ValidTestClass"));
    }

    // --- TestCustomFieldValidation ---

    /// <summary>Verify reserved field name (GCRecord) is rejected.</summary>
    [Fact]
    public async Task Test_05_ReservedFieldNameRejected()
    {
        await NavToCustomFieldAsync();
        var lv = new ListViewPage(_page);
        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(1000);

        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Field Name", "GCRecord");
        var (saved, errorText) = await TrySaveAndCheckValidationAsync();

        Assert.False(saved, "Save should fail for reserved field name GCRecord");
        Assert.True(errorText.ToLowerInvariant().Contains("reserved") || errorText.Contains("Validation"));
    }

    /// <summary>Verify field name with special characters is rejected.</summary>
    [Fact]
    public async Task Test_06_InvalidFieldNameRejected()
    {
        await NavToCustomFieldAsync();
        var lv = new ListViewPage(_page);
        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(1000);

        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Field Name", "my-field");
        var (saved, errorText) = await TrySaveAndCheckValidationAsync();

        Assert.False(saved, "Save should fail for field name with hyphens");
        Assert.True(errorText.Contains("valid C# identifier") || errorText.Contains("Validation"));
    }

    // --- TestTypeNameDropdown ---

    /// <summary>Verify TypeName field has predefined type values (default System.String).</summary>
    [Fact]
    public async Task Test_07_TypeDropdownHasValues()
    {
        await NavToCustomFieldAsync();
        var lv = new ListViewPage(_page);
        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(2000);

        // The TypeName field should default to "System.String" (from the entity default).
        // Also verify the field container exists — XAF renders it with PredefinedValues.
        // Try multiple possible data-item-name values
        ILocator container = _page.Locator(".dxbl-fl-ctrl:has([data-item-name='TypeName'])");
        foreach (var attrName in new[] { "Type Name", "TypeName" })
        {
            container = _page.Locator($".dxbl-fl-ctrl:has([data-item-name='{attrName}'])");
            if (await container.CountAsync() > 0)
                break;
        }

        Assert.True(await container.CountAsync() > 0, "TypeName field container should exist");

        // Check if the default value System.String is shown in the container
        var containerText = await container.First.InnerTextAsync();
        var containerHtml = await container.First.InnerHTMLAsync();

        // Look for System.String in input value, inner text, or HTML
        var hasDefault = containerText.Contains("System.String") || containerHtml.Contains("System.String");

        // Also try getting the input value
        var inputEl = container.First.Locator("input");
        var inputCount = await inputEl.CountAsync();
        for (var i = 0; i < inputCount; i++)
        {
            var val = await inputEl.Nth(i).InputValueAsync();
            if (val.Contains("System.String"))
            {
                hasDefault = true;
                break;
            }
        }

        Assert.True(hasDefault,
            $"TypeName should default to System.String. Text: {containerText[..Math.Min(200, containerText.Length)]}");
    }

    // --- TestTestCompileAction ---

    /// <summary>Verify Test Compile All on the ListView shows success for all runtime classes.</summary>
    [Fact]
    public async Task Test_08_TestCompileSuccess()
    {
        var (nav, lv) = await NavToCustomClassAsync();

        // Ensure at least one runtime class exists (ValidTestClass from Test_04)
        if (!await lv.HasRowWithTextAsync("ValidTestClass"))
        {
            await lv.ClickNewAsync();
            await _page.WaitForTimeoutAsync(1000);
            var detail = new DetailViewPage(_page);
            await detail.FillFieldAsync("Class Name", "ValidTestClass");
            await detail.FillFieldAsync("Navigation Group", "Test");
            await detail.ClickSaveAsync();
            await _page.WaitForTimeoutAsync(2000);
            await nav.NavigateToAsync("Schema Management", "Custom Class");
            await lv.WaitForGridAsync();
        }

        await _page.WaitForTimeoutAsync(1000);

        // Click the Test Compile All action on the ListView.
        // data-action-name holds the Action's rendered CAPTION ("Test Compile All"), not its
        // Id ("TestCompile" - TestCompileController.cs). Confirmed in DX 26.1 source:
        // DxActionItemActionControlBase.SetImageAndCaption(paintStyle, imageName, caption) calls
        // both SetCaptionCore(caption) and SetDataActionNameAttribute(caption) with the same
        // caption value (DevExpress.ExpressApp.Blazor\Templates\ActionControls\
        // DxActionItemActionControlBase.cs:78-80). New/Save/Delete locators elsewhere happen to
        // work because their Caption equals their Id.
        var compileBtn = _page.Locator("dxbl-toolbar-item > button[data-action-name=\"Test Compile All\"], dxbl-bar-item > button[data-action-name=\"Test Compile All\"]");
        Assert.True(await compileBtn.CountAsync() > 0, "Test Compile All action should be visible on CustomClass ListView");

        await compileBtn.First.ClickAsync();
        await _page.WaitForTimeoutAsync(3000);

        // Check for success message
        var bodyText = (await _page.Locator("body").InnerTextAsync()).ToLowerInvariant();
        Assert.True(bodyText.Contains("successful") || bodyText.Contains("success") || bodyText.Contains("compiled"),
            $"Should show compilation success message. Page text: {bodyText[..Math.Min(500, bodyText.Length)]}");
    }

    // --- TestCleanup ---

    /// <summary>Remove all test data created during Phase 3 tests.</summary>
    [Fact]
    public async Task Test_99_Cleanup()
    {
        await NavToCustomClassAsync();
        foreach (var name in new[] { "ValidTestClass", "123Invalid", "class", "BaseObject" })
        {
            await DeleteIfExistsAsync(name);
        }
    }
}
