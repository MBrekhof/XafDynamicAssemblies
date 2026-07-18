namespace XafDynamicAssemblies.Tests.Pages;

/// <summary>
/// Page object for XAF DetailView (form) interactions.
/// Ported from tests/pages/detail_view_page.py.
///
/// XAF Blazor form fields use .dxbl-fl-ctrl containers with a hidden
/// &lt;div data-item-name="FieldLabel" class="d-none"&gt; for identification,
/// followed by dxbl-input-editor or dxbl-combo-box components.
/// </summary>
public class DetailViewPage : BasePage
{
    public DetailViewPage(IPage page) : base(page) { }

    /// <summary>Fill a text field identified by its label (data-item-name).</summary>
    public async Task FillFieldAsync(string label, string value)
    {
        var field = await FindInputByLabelAsync(label);
        await field.ClickAsync();
        await field.FillAsync(value);
        await field.PressAsync("Tab");
        await Page.WaitForTimeoutAsync(300);
    }

    /// <summary>Clear a field identified by its label.</summary>
    public async Task ClearFieldAsync(string label)
    {
        var field = await FindInputByLabelAsync(label);
        await field.ClickAsync();
        await field.FillAsync("");
        await field.PressAsync("Tab");
        await Page.WaitForTimeoutAsync(300);
    }

    /// <summary>Get the current value of a text input field by label.</summary>
    public async Task<string> GetFieldValueAsync(string label)
    {
        var field = await FindInputByLabelAsync(label);
        return await field.InputValueAsync();
    }

    /// <summary>Get displayed text of a field (works for dropdowns too).</summary>
    public async Task<string> GetFieldTextAsync(string label)
    {
        var container = await FindContainerByLabelAsync(label);
        var inputEl = container.Locator("input:not([type='hidden'])");
        if (await inputEl.CountAsync() > 0)
        {
            var val = await inputEl.First.InputValueAsync();
            if (!string.IsNullOrEmpty(val))
                return val;
        }
        return await container.InnerTextAsync();
    }

    /// <summary>Set a checkbox field by label.</summary>
    public async Task SetCheckboxAsync(string label, bool isChecked)
    {
        var container = await FindContainerByLabelAsync(label);
        var checkbox = container.Locator("input[type='checkbox']").First;
        if (await checkbox.IsCheckedAsync() != isChecked)
            await checkbox.ClickAsync();
        await Page.WaitForTimeoutAsync(300);
    }

    /// <summary>Find the .dxbl-fl-ctrl container for a field by its data-item-name.</summary>
    private async Task<ILocator> FindContainerByLabelAsync(string label)
    {
        var container = Page.Locator($".dxbl-fl-ctrl:has([data-item-name='{label}'])");
        if (await container.CountAsync() > 0)
            return container.First;
        throw new InvalidOperationException($"Could not find form container with label: {label}");
    }

    /// <summary>Find an input element by its XAF form layout data-item-name.</summary>
    private async Task<ILocator> FindInputByLabelAsync(string label)
    {
        // Primary: find by data-item-name attribute within form layout control
        var field = Page.Locator(
            $".dxbl-fl-ctrl:has([data-item-name='{label}']) input:not([type='hidden']):not([type='checkbox'])");
        if (await field.CountAsync() > 0)
            return field.First;

        // Also try textarea
        field = Page.Locator($".dxbl-fl-ctrl:has([data-item-name='{label}']) textarea");
        if (await field.CountAsync() > 0)
            return field.First;

        throw new InvalidOperationException($"Could not find input field with label: {label}");
    }
}
