from playwright.sync_api import Page


class BasePage:
    """Base page object with common XAF Blazor interactions."""

    def __init__(self, page: Page):
        self.page = page

    def wait_for_loading(self, timeout: int = 10000):
        """Wait for XAF loading indicators to disappear."""
        self.page.wait_for_load_state("networkidle")
        self.page.wait_for_timeout(500)

    def click_new(self):
        """Click the New action button in the toolbar."""
        # XAF Blazor toolbar uses <dxbl-toolbar-item text="New">
        self.page.locator('dxbl-toolbar-item[text="New"]').first.click()
        self.wait_for_loading()

    def click_save(self):
        """Click Save action in the toolbar."""
        self.page.locator('dxbl-toolbar-item[text="Save"]').first.click()
        self.wait_for_loading()

    def click_delete(self):
        """Click the Delete action button in the toolbar."""
        self.page.locator('dxbl-toolbar-item[text="Delete"]').first.click()
        self.page.wait_for_timeout(500)

    def confirm_delete(self):
        """Confirm the delete dialog by clicking Yes/OK."""
        # XAF delete confirmation popup
        self.page.locator(
            ".dxbl-popup-footer button:has-text('Yes'), "
            ".dxbl-popup-footer button:has-text('OK'), "
            "button.dxbl-btn:has-text('Yes')"
        ).first.click(timeout=5000)
        self.wait_for_loading()

    def click_action(self, action_text: str):
        """Click a named action button in the toolbar."""
        self.page.locator(f'dxbl-toolbar-item[text="{action_text}"]').first.click()
        self.wait_for_loading()

    def wait_for_view_loaded(self, timeout: int = 15000):
        """Wait for the current view to finish loading."""
        self.page.wait_for_load_state("networkidle")
        self.page.wait_for_timeout(500)
