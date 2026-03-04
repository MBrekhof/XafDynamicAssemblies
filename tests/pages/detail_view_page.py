from playwright.sync_api import Page
from .base_page import BasePage


class DetailViewPage(BasePage):
    """Page object for XAF DetailView (form) interactions.

    XAF Blazor form fields use .dxbl-fl-ctrl containers with a hidden
    <div data-item-name="FieldLabel" class="d-none"> for identification,
    followed by dxbl-input-editor or dxbl-combo-box components.
    """

    def __init__(self, page: Page):
        super().__init__(page)

    def fill_field(self, label: str, value: str):
        """Fill a text field identified by its label (data-item-name)."""
        field = self._find_input_by_label(label)
        field.click()
        field.fill(value)
        field.press("Tab")
        self.page.wait_for_timeout(300)

    def clear_field(self, label: str):
        """Clear a field identified by its label."""
        field = self._find_input_by_label(label)
        field.click()
        field.fill("")
        field.press("Tab")
        self.page.wait_for_timeout(300)

    def get_field_value(self, label: str) -> str:
        """Get the current value of a text input field by label."""
        field = self._find_input_by_label(label)
        return field.input_value()

    def get_field_text(self, label: str) -> str:
        """Get displayed text of a field (works for dropdowns too)."""
        container = self._find_container_by_label(label)
        input_el = container.locator("input:not([type='hidden'])")
        if input_el.count() > 0:
            val = input_el.first.input_value()
            if val:
                return val
        return container.inner_text()

    def set_checkbox(self, label: str, checked: bool):
        """Set a checkbox field by label."""
        container = self._find_container_by_label(label)
        checkbox = container.locator("input[type='checkbox']").first
        if checkbox.is_checked() != checked:
            checkbox.click()
        self.page.wait_for_timeout(300)

    def _find_container_by_label(self, label: str):
        """Find the .dxbl-fl-ctrl container for a field by its data-item-name."""
        container = self.page.locator(
            f".dxbl-fl-ctrl:has([data-item-name='{label}'])"
        )
        if container.count() > 0:
            return container.first
        raise ValueError(f"Could not find form container with label: {label}")

    def _find_input_by_label(self, label: str):
        """Find an input element by its XAF form layout data-item-name."""
        # Primary: find by data-item-name attribute within form layout control
        field = self.page.locator(
            f".dxbl-fl-ctrl:has([data-item-name='{label}']) input:not([type='hidden']):not([type='checkbox'])"
        )
        if field.count() > 0:
            return field.first

        # Also try textarea
        field = self.page.locator(
            f".dxbl-fl-ctrl:has([data-item-name='{label}']) textarea"
        )
        if field.count() > 0:
            return field.first

        raise ValueError(f"Could not find input field with label: {label}")
