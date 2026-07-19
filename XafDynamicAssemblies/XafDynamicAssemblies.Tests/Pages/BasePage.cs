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

    // DevExpress Blazor 26.1: the Model.xafml FormStyle="Ribbon" toolbar renders actions under
    // <dxbl-bar-item> (not <dxbl-toolbar-item>), and the caption is no longer exposed as a
    // `text` attribute on the wrapper — only as a <span> inside the inner <button>. The
    // reliable, style-agnostic hook is `data-action-name`, which XAF stamps on the button with
    // the Action's Id (ActionBase.Id — not its Caption). An internal "virtual toolbar" clone
    // used for adaptive-layout measurement duplicates every real button off-screen inside a
    // plain <div> instead of the custom element, so scoping to a direct `dxbl-toolbar-item`/
    // `dxbl-bar-item` parent excludes it. Verified against the live 26.1 DOM (see
    // dx-upgrade-report.md) — dxdocs has no page documenting `data-action-name` directly.
    private static string ActionButtonSelector(string actionId) =>
        $"dxbl-toolbar-item > button[data-action-name=\"{actionId}\"], " +
        $"dxbl-bar-item > button[data-action-name=\"{actionId}\"]";

    /// <summary>Click the New action button in the toolbar.</summary>
    public async Task ClickNewAsync()
    {
        await Page.Locator(ActionButtonSelector("New")).First.ClickAsync();
        await WaitForLoadingAsync();
    }

    /// <summary>Click Save action in the toolbar.</summary>
    public async Task ClickSaveAsync()
    {
        await Page.Locator(ActionButtonSelector("Save")).First.ClickAsync();
        await WaitForLoadingAsync();
    }

    /// <summary>Click the Delete action button in the toolbar.</summary>
    public async Task ClickDeleteAsync()
    {
        await Page.Locator(ActionButtonSelector("Delete")).First.ClickAsync();
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

    /// <summary>Click a toolbar action button, identified by its Action Id (ActionBase.Id — not its Caption).</summary>
    public async Task ClickActionAsync(string actionId)
    {
        await Page.Locator(ActionButtonSelector(actionId)).First.ClickAsync();
        await WaitForLoadingAsync();
    }
}
