namespace XafDynamicAssemblies.Tests.Pages;

/// <summary>
/// Page object for XAF ListView (DxGrid) interactions.
/// Ported from tests/pages/list_view_page.py.
/// </summary>
public class ListViewPage : BasePage
{
    private const string GridRow = ".dxbl-grid-table tbody tr[data-visible-index]";

    public ListViewPage(IPage page) : base(page) { }

    /// <summary>Wait for a visible grid to appear (handles multiple grids in DOM).</summary>
    public async Task WaitForGridAsync(int timeout = 15_000)
    {
        await Page.WaitForFunctionAsync(
            "() => Array.from(document.querySelectorAll('.dxbl-grid')).some(g => g.offsetWidth > 0)",
            null,
            new() { Timeout = timeout });
        await Page.WaitForTimeoutAsync(500);
    }

    /// <summary>Get the number of data rows in the grid.</summary>
    public async Task<int> GetRowCountAsync()
    {
        return await Page.Locator(GridRow).CountAsync();
    }

    /// <summary>Click a row by its zero-based index.</summary>
    public async Task ClickRowAsync(int index)
    {
        await Page.Locator(GridRow).Nth(index).ClickAsync();
        await Page.WaitForTimeoutAsync(300);
    }

    /// <summary>Double-click a row to open its detail view.</summary>
    public async Task DoubleClickRowAsync(int index)
    {
        await Page.Locator(GridRow).Nth(index).DblClickAsync();
        await WaitForLoadingAsync();
    }

    /// <summary>Find the index of the first row containing the given text. Returns -1 if not found.</summary>
    public async Task<int> FindRowWithTextAsync(string text)
    {
        var rows = Page.Locator(GridRow);
        var count = await rows.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var rowText = await rows.Nth(i).InnerTextAsync();
            if (rowText.Contains(text))
                return i;
        }
        return -1;
    }

    /// <summary>Click the first row containing the given text.</summary>
    public async Task SelectRowWithTextAsync(string text)
    {
        var index = await FindRowWithTextAsync(text);
        if (index >= 0)
            await ClickRowAsync(index);
        else
            throw new InvalidOperationException($"No row found containing text: {text}");
    }

    /// <summary>Double-click the first row containing the given text to open detail view.</summary>
    public async Task DoubleClickRowWithTextAsync(string text)
    {
        var index = await FindRowWithTextAsync(text);
        if (index >= 0)
            await DoubleClickRowAsync(index);
        else
            throw new InvalidOperationException($"No row found containing text: {text}");
    }

    /// <summary>Check if any row contains the given text.</summary>
    public async Task<bool> HasRowWithTextAsync(string text)
    {
        return await FindRowWithTextAsync(text) >= 0;
    }

    /// <summary>Check if the grid shows 'No data to display'.</summary>
    public async Task<bool> HasNoDataAsync()
    {
        return await Page.Locator("text=No data to display").CountAsync() > 0;
    }
}
