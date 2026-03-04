from playwright.sync_api import Page
from .base_page import BasePage


class ListViewPage(BasePage):
    """Page object for XAF ListView (DxGrid) interactions."""

    GRID_ROW = ".dxbl-grid-table tbody tr[data-visible-index]"

    def __init__(self, page: Page):
        super().__init__(page)

    def wait_for_grid(self, timeout: int = 15000):
        """Wait for a visible grid to appear (handles multiple grids in DOM)."""
        self.page.wait_for_function(
            "() => Array.from(document.querySelectorAll('.dxbl-grid')).some(g => g.offsetWidth > 0)",
            timeout=timeout
        )
        self.page.wait_for_timeout(500)

    def get_row_count(self) -> int:
        """Get the number of data rows in the grid."""
        return self.page.locator(self.GRID_ROW).count()

    def click_row(self, index: int):
        """Click a row by its zero-based index."""
        self.page.locator(self.GRID_ROW).nth(index).click()
        self.page.wait_for_timeout(300)

    def double_click_row(self, index: int):
        """Double-click a row to open its detail view."""
        self.page.locator(self.GRID_ROW).nth(index).dblclick()
        self.wait_for_loading()

    def find_row_with_text(self, text: str) -> int:
        """Find the index of the first row containing the given text. Returns -1 if not found."""
        rows = self.page.locator(self.GRID_ROW)
        count = rows.count()
        for i in range(count):
            if text in rows.nth(i).inner_text():
                return i
        return -1

    def select_row_with_text(self, text: str):
        """Click the first row containing the given text."""
        idx = self.find_row_with_text(text)
        if idx >= 0:
            self.click_row(idx)
        else:
            raise ValueError(f"No row found containing text: {text}")

    def double_click_row_with_text(self, text: str):
        """Double-click the first row containing the given text to open detail view."""
        idx = self.find_row_with_text(text)
        if idx >= 0:
            self.double_click_row(idx)
        else:
            raise ValueError(f"No row found containing text: {text}")

    def has_row_with_text(self, text: str) -> bool:
        """Check if any row contains the given text."""
        return self.find_row_with_text(text) >= 0

    def has_no_data(self) -> bool:
        """Check if the grid shows 'No data to display'."""
        return self.page.locator("text=No data to display").count() > 0
