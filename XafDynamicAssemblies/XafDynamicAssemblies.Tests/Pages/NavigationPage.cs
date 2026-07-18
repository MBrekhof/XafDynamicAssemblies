namespace XafDynamicAssemblies.Tests.Pages;

/// <summary>
/// Page object for XAF Blazor navigation pane interactions.
/// Ported from tests/pages/navigation_page.py.
///
/// XAF Blazor uses a DevExpress accordion with dxbl-group-control groups.
/// Groups are lazy-loaded: child items only appear in the DOM after expansion.
/// IMPORTANT: Playwright's Force click option does NOT work with Blazor's event
/// system. We must use native DOM .click() via Page.EvaluateAsync() instead.
/// </summary>
public class NavigationPage : BasePage
{
    public NavigationPage(IPage page) : base(page) { }

    /// <summary>Expand an accordion group using native JS click (works with Blazor).</summary>
    private async Task<bool> ExpandGroupJsAsync(string group)
    {
        // ponytail: group name passed as an EvaluateAsync argument (JSON-encoded) rather than
        // Python's raw f-string interpolation into the JS source — functionally identical for
        // the plain-text group names this suite uses, but also safe for names containing quotes.
        var result = await Page.EvaluateAsync<string>(@"(groupName) => {
            const groups = document.querySelectorAll('dxbl-group-control.xaf-nav-item');
            for (const g of groups) {
                const link = g.querySelector('.xaf-nav-link');
                if (link && link.textContent.trim() === groupName) {
                    if (g.classList.contains('expanded')) {
                        return 'already_expanded';
                    }
                    const btn = g.querySelector('.dxbl-group-expand-btn');
                    if (btn) {
                        btn.click();
                        return 'clicked';
                    }
                    // Fallback: click the group header
                    const header = g.querySelector('.dxbl-group-header');
                    if (header) {
                        header.click();
                        return 'header_clicked';
                    }
                    return 'no_button';
                }
            }
            return 'not_found';
        }", group);

        if (result is "clicked" or "header_clicked")
        {
            await Page.WaitForTimeoutAsync(1500);
            return true;
        }
        return result == "already_expanded";
    }

    /// <summary>Navigate to a specific item within a navigation group.</summary>
    public async Task NavigateToAsync(string group, string item)
    {
        await ExpandGroupJsAsync(group);

        // After expansion, child items are now in the DOM.
        // Find accordion items with the matching text and click via click-area overlay.
        var items = Page.Locator(".dxbl-accordion-item:not(.has-children) .dxbl-accordion-item-content");
        var count = await items.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var el = items.Nth(i);
            string text;
            try
            {
                text = await el.Locator(".xaf-nav-link").TextContentAsync(new() { Timeout = 2000 }) ?? "";
            }
            catch
            {
                continue;
            }

            if (text.Contains(item, StringComparison.OrdinalIgnoreCase))
            {
                // Use JS click for reliability with Blazor
                var clickArea = el.Locator(".xaf-navigation-link-click-area");
                if (await clickArea.CountAsync() > 0)
                {
                    var handle = await clickArea.First.ElementHandleAsync();
                    await Page.EvaluateAsync("el => el.click()", handle);
                }
                else
                {
                    var handle = await el.Locator(".xaf-nav-link").First.ElementHandleAsync();
                    await Page.EvaluateAsync("el => el.click()", handle);
                }
                await WaitForLoadingAsync();
                return;
            }
        }

        // Fallback: direct JS click on nav-link
        var navLink = Page.Locator($".dxbl-accordion-item .xaf-nav-link:has-text('{item}')");
        if (await navLink.CountAsync() == 0)
            navLink = Page.Locator($".xaf-nav-link:has-text('{item}')");

        if (await navLink.CountAsync() > 0)
        {
            var handle = await navLink.First.ElementHandleAsync();
            await Page.EvaluateAsync("el => el.click()", handle);
        }
        else
        {
            throw new InvalidOperationException($"Navigation item '{item}' not found in group '{group}'");
        }
        await WaitForLoadingAsync();
    }

    /// <summary>Navigate directly to a navigation item by text (expands parent group if needed).</summary>
    public async Task NavigateToItemAsync(string item)
    {
        // First try clicking directly if visible
        var navLink = Page.Locator($".dxbl-accordion-item .xaf-nav-link:has-text('{item}')");
        if (await navLink.CountAsync() > 0)
        {
            var handle = await navLink.First.ElementHandleAsync();
            await Page.EvaluateAsync("el => el.click()", handle);
            await WaitForLoadingAsync();
            return;
        }

        // Item not visible — try expanding all groups to find it
        var groups = Page.Locator("dxbl-group-control.xaf-nav-item");
        var groupCount = await groups.CountAsync();
        for (var i = 0; i < groupCount; i++)
        {
            var grp = groups.Nth(i);
            var cls = await grp.GetAttributeAsync("class") ?? "";
            if (!cls.Contains("expanded"))
            {
                var link = grp.Locator(".xaf-nav-link").First;
                var groupText = await link.TextContentAsync() ?? "";
                await ExpandGroupJsAsync(groupText.Trim());
            }
        }

        navLink = Page.Locator($".dxbl-accordion-item .xaf-nav-link:has-text('{item}')");
        if (await navLink.CountAsync() > 0)
        {
            var handle = await navLink.First.ElementHandleAsync();
            await Page.EvaluateAsync("el => el.click()", handle);
            await WaitForLoadingAsync();
        }
        else
        {
            throw new InvalidOperationException($"Navigation item '{item}' not found");
        }
    }

    /// <summary>Check if a navigation group is visible.</summary>
    public async Task<bool> IsGroupVisibleAsync(string group)
    {
        return await Page.Locator($".xaf-nav-link:has-text('{group}')").CountAsync() > 0;
    }

    /// <summary>Check if a navigation item is visible (may need group to be expanded first).</summary>
    public async Task<bool> IsItemVisibleAsync(string item)
    {
        return await Page.Locator($".xaf-nav-link:has-text('{item}')").CountAsync() > 0;
    }
}
