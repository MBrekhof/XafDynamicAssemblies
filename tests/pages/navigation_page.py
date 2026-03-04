from playwright.sync_api import Page
from .base_page import BasePage


class NavigationPage(BasePage):
    """Page object for XAF Blazor navigation pane interactions.

    XAF Blazor uses a DevExpress accordion with dxbl-group-control groups.
    Groups are lazy-loaded: child items only appear in the DOM after expansion.
    IMPORTANT: Playwright's force=True click does NOT work with Blazor's event
    system. We must use native DOM .click() via page.evaluate() instead.
    """

    def __init__(self, page: Page):
        super().__init__(page)

    def _expand_group_js(self, group: str) -> bool:
        """Expand an accordion group using native JS click (works with Blazor)."""
        result = self.page.evaluate(f"""() => {{
            const groups = document.querySelectorAll('dxbl-group-control.xaf-nav-item');
            for (const g of groups) {{
                const link = g.querySelector('.xaf-nav-link');
                if (link && link.textContent.trim() === '{group}') {{
                    if (g.classList.contains('expanded')) {{
                        return 'already_expanded';
                    }}
                    const btn = g.querySelector('.dxbl-group-expand-btn');
                    if (btn) {{
                        btn.click();
                        return 'clicked';
                    }}
                    // Fallback: click the group header
                    const header = g.querySelector('.dxbl-group-header');
                    if (header) {{
                        header.click();
                        return 'header_clicked';
                    }}
                    return 'no_button';
                }}
            }}
            return 'not_found';
        }}""")
        if result in ('clicked', 'header_clicked'):
            self.page.wait_for_timeout(1500)
            return True
        return result == 'already_expanded'

    def navigate_to(self, group: str, item: str):
        """Navigate to a specific item within a navigation group."""
        self._expand_group_js(group)

        # After expansion, child items are now in the DOM.
        # Find accordion items with the matching text and click via click-area overlay.
        items = self.page.locator(
            ".dxbl-accordion-item:not(.has-children) .dxbl-accordion-item-content"
        )
        for i in range(items.count()):
            el = items.nth(i)
            try:
                text = el.locator(".xaf-nav-link").text_content(timeout=2000) or ""
            except Exception:
                continue
            if item.lower() in text.lower():
                # Use JS click for reliability with Blazor
                click_area = el.locator(".xaf-navigation-link-click-area")
                if click_area.count() > 0:
                    self.page.evaluate(
                        "el => el.click()",
                        click_area.first.element_handle()
                    )
                else:
                    self.page.evaluate(
                        "el => el.click()",
                        el.locator(".xaf-nav-link").first.element_handle()
                    )
                self.wait_for_loading()
                return

        # Fallback: direct JS click on nav-link
        nav_link = self.page.locator(
            f".dxbl-accordion-item .xaf-nav-link:has-text('{item}')"
        )
        if nav_link.count() == 0:
            nav_link = self.page.locator(f".xaf-nav-link:has-text('{item}')")
        if nav_link.count() > 0:
            self.page.evaluate(
                "el => el.click()",
                nav_link.first.element_handle()
            )
        else:
            raise ValueError(f"Navigation item '{item}' not found in group '{group}'")
        self.wait_for_loading()

    def navigate_to_item(self, item: str):
        """Navigate directly to a navigation item by text (expands parent group if needed)."""
        # First try clicking directly if visible
        nav_link = self.page.locator(
            f".dxbl-accordion-item .xaf-nav-link:has-text('{item}')"
        )
        if nav_link.count() > 0:
            self.page.evaluate(
                "el => el.click()",
                nav_link.first.element_handle()
            )
            self.wait_for_loading()
            return

        # Item not visible — try expanding all groups to find it
        groups = self.page.locator("dxbl-group-control.xaf-nav-item")
        for i in range(groups.count()):
            grp = groups.nth(i)
            cls = grp.get_attribute("class") or ""
            if "expanded" not in cls:
                link = grp.locator(".xaf-nav-link").first
                group_text = link.text_content() or ""
                self._expand_group_js(group_text.strip())

        nav_link = self.page.locator(
            f".dxbl-accordion-item .xaf-nav-link:has-text('{item}')"
        )
        if nav_link.count() > 0:
            self.page.evaluate(
                "el => el.click()",
                nav_link.first.element_handle()
            )
            self.wait_for_loading()
        else:
            raise ValueError(f"Navigation item '{item}' not found")

    def is_group_visible(self, group: str) -> bool:
        """Check if a navigation group is visible."""
        return self.page.locator(f".xaf-nav-link:has-text('{group}')").count() > 0

    def is_item_visible(self, item: str) -> bool:
        """Check if a navigation item is visible (may need group to be expanded first)."""
        return self.page.locator(f".xaf-nav-link:has-text('{item}')").count() > 0
