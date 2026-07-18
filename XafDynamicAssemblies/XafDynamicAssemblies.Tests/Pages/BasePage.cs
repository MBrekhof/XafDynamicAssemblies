namespace XafDynamicAssemblies.Tests.Pages;

/// <summary>
/// Base page object with common XAF Blazor interactions.
/// Ported from tests/pages/base_page.py.
/// </summary>
public class BasePage
{
    protected readonly IPage Page;

    public BasePage(IPage page) => Page = page;

    /// <summary>Wait for XAF loading indicators to disappear.</summary>
    // ponytail: `timeout` is unused here too — base_page.py's wait_for_loading() takes the
    // same parameter and never passes it to wait_for_load_state(). Kept for signature parity.
    public async Task WaitForLoadingAsync(int timeout = 10_000)
    {
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Page.WaitForTimeoutAsync(500);
    }

    /// <summary>Click the New action button in the toolbar.</summary>
    public async Task ClickNewAsync()
    {
        await Page.Locator("dxbl-toolbar-item[text=\"New\"]").First.ClickAsync();
        await WaitForLoadingAsync();
    }

    /// <summary>Click Save action in the toolbar.</summary>
    public async Task ClickSaveAsync()
    {
        await Page.Locator("dxbl-toolbar-item[text=\"Save\"]").First.ClickAsync();
        await WaitForLoadingAsync();
    }

    /// <summary>Click the Delete action button in the toolbar.</summary>
    public async Task ClickDeleteAsync()
    {
        await Page.Locator("dxbl-toolbar-item[text=\"Delete\"]").First.ClickAsync();
        await Page.WaitForTimeoutAsync(500);
    }

    /// <summary>Confirm the delete dialog by clicking Yes/OK.</summary>
    public async Task ConfirmDeleteAsync()
    {
        await Page.Locator(
            ".dxbl-popup-footer button:has-text('Yes'), " +
            ".dxbl-popup-footer button:has-text('OK'), " +
            "button.dxbl-btn:has-text('Yes')"
        ).First.ClickAsync(new() { Timeout = 5000 });
        await WaitForLoadingAsync();
    }

    /// <summary>Click a named action button in the toolbar.</summary>
    public async Task ClickActionAsync(string actionText)
    {
        await Page.Locator($"dxbl-toolbar-item[text=\"{actionText}\"]").First.ClickAsync();
        await WaitForLoadingAsync();
    }
}
