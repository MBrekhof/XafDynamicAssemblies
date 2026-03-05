"""Phase 3 Tests: Validation rules, TypeName dropdown, and Test Compile action.

Verifies that:
- Invalid class names are rejected (digits, keywords, reserved names)
- Invalid field names are rejected (reserved names like Id, GCRecord)
- TypeName dropdown shows supported types
- Test Compile action validates Roslyn compilation from the UI
"""
import pytest
import sys
import os

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from pages.navigation_page import NavigationPage
from pages.list_view_page import ListViewPage
from pages.detail_view_page import DetailViewPage


def nav_to_custom_class(page):
    nav = NavigationPage(page)
    nav.navigate_to("Schema Management", "Custom Class")
    lv = ListViewPage(page)
    lv.wait_for_grid()
    return nav, lv


def nav_to_custom_field(page):
    nav = NavigationPage(page)
    nav.navigate_to("Schema Management", "Custom Field")
    lv = ListViewPage(page)
    lv.wait_for_grid()
    return nav, lv


def delete_if_exists(page, text):
    lv = ListViewPage(page)
    if lv.has_row_with_text(text):
        lv.select_row_with_text(text)
        lv.click_delete()
        lv.confirm_delete()
        page.wait_for_timeout(500)


def try_save_and_check_validation(page, timeout=3000):
    """Click Save and check if a validation error appeared.

    Returns (saved_ok, error_text):
    - saved_ok=True means save succeeded (no validation error)
    - saved_ok=False means validation blocked the save, error_text has the message
    """
    page.locator('dxbl-toolbar-item[text="Save"]').first.click()
    page.wait_for_timeout(1500)

    # XAF Blazor shows validation errors in a popup/window with error details.
    # Check for validation error indicators:

    # 1. Check for XAF validation result window (contains error messages)
    validation_window = page.locator(".dxbl-popup-content, .dxbl-window")
    if validation_window.count() > 0:
        for i in range(validation_window.count()):
            text = validation_window.nth(i).inner_text()
            if "must be" in text.lower() or "cannot be" in text.lower() or "reserved" in text.lower() or "conflicts" in text.lower() or "valid" in text.lower() or "error" in text.lower():
                return False, text

    # 2. Check for any visible popup with validation-related text
    body_text = page.locator("body").inner_text()
    validation_keywords = [
        "must be a valid C# identifier",
        "cannot be a C# keyword",
        "conflicts with a built-in type",
        "Field Name is reserved",
        "must be a supported CLR type",
        "Validation"
    ]
    for keyword in validation_keywords:
        if keyword in body_text:
            return False, body_text

    # 3. Check if we're still on the detail view (save didn't navigate away)
    # If a .dxbl-fl-ctrl is still visible, we might still be on detail view
    # But this alone doesn't confirm validation error

    return True, ""


class TestCustomClassValidation:
    """Tests for CustomClass name validation rules."""

    def test_01_invalid_class_name_digits(self, page):
        """Verify class name starting with digit is rejected."""
        nav, lv = nav_to_custom_class(page)
        lv.click_new()
        page.wait_for_timeout(1000)

        detail = DetailViewPage(page)
        detail.fill_field("Class Name", "123Invalid")
        saved, error_text = try_save_and_check_validation(page)

        assert not saved, "Save should fail for class name starting with digit"
        assert "valid C# identifier" in error_text or "Validation" in error_text

    def test_02_csharp_keyword_rejected(self, page):
        """Verify C# keyword as class name is rejected."""
        nav, lv = nav_to_custom_class(page)
        lv.click_new()
        page.wait_for_timeout(1000)

        detail = DetailViewPage(page)
        detail.fill_field("Class Name", "class")
        saved, error_text = try_save_and_check_validation(page)

        assert not saved, "Save should fail for C# keyword class name"
        assert "keyword" in error_text.lower() or "Validation" in error_text

    def test_03_reserved_type_rejected(self, page):
        """Verify reserved type name (BaseObject) is rejected."""
        nav, lv = nav_to_custom_class(page)
        lv.click_new()
        page.wait_for_timeout(1000)

        detail = DetailViewPage(page)
        detail.fill_field("Class Name", "BaseObject")
        saved, error_text = try_save_and_check_validation(page)

        assert not saved, "Save should fail for reserved type name"
        assert "conflicts" in error_text.lower() or "built-in" in error_text.lower() or "Validation" in error_text

    def test_04_valid_class_name_accepted(self, page):
        """Verify a valid class name is accepted."""
        nav, lv = nav_to_custom_class(page)
        delete_if_exists(page, "ValidTestClass")

        lv.click_new()
        page.wait_for_timeout(1000)

        detail = DetailViewPage(page)
        detail.fill_field("Class Name", "ValidTestClass")
        detail.fill_field("Navigation Group", "Test")
        saved, error_text = try_save_and_check_validation(page)

        assert saved, f"Save should succeed for valid class name. Error: {error_text}"

        # Navigate back and verify
        nav.navigate_to("Schema Management", "Custom Class")
        lv.wait_for_grid()
        page.wait_for_timeout(1000)
        assert lv.has_row_with_text("ValidTestClass")


class TestCustomFieldValidation:
    """Tests for CustomField name validation rules."""

    def test_05_reserved_field_name_rejected(self, page):
        """Verify reserved field name (GCRecord) is rejected."""
        nav, lv = nav_to_custom_field(page)
        lv.click_new()
        page.wait_for_timeout(1000)

        detail = DetailViewPage(page)
        detail.fill_field("Field Name", "GCRecord")
        saved, error_text = try_save_and_check_validation(page)

        assert not saved, "Save should fail for reserved field name GCRecord"
        assert "reserved" in error_text.lower() or "Validation" in error_text

    def test_06_invalid_field_name_rejected(self, page):
        """Verify field name with special characters is rejected."""
        nav, lv = nav_to_custom_field(page)
        lv.click_new()
        page.wait_for_timeout(1000)

        detail = DetailViewPage(page)
        detail.fill_field("Field Name", "my-field")
        saved, error_text = try_save_and_check_validation(page)

        assert not saved, "Save should fail for field name with hyphens"
        assert "valid C# identifier" in error_text or "Validation" in error_text


class TestTypeNameDropdown:
    """Tests for TypeName predefined values dropdown."""

    def test_07_type_dropdown_has_values(self, page):
        """Verify TypeName field has predefined type values (default System.String)."""
        nav, lv = nav_to_custom_field(page)
        lv.click_new()
        page.wait_for_timeout(2000)

        # The TypeName field should default to "System.String" (from the entity default).
        # Also verify the field container exists — XAF renders it with PredefinedValues.
        # Try multiple possible data-item-name values
        for attr_name in ["Type Name", "TypeName"]:
            container = page.locator(f".dxbl-fl-ctrl:has([data-item-name='{attr_name}'])")
            if container.count() > 0:
                break

        assert container.count() > 0, "TypeName field container should exist"

        # Check if the default value System.String is shown in the container
        container_text = container.first.inner_text()
        container_html = container.first.inner_html()

        # Look for System.String in input value, inner text, or HTML
        has_default = "System.String" in container_text or "System.String" in container_html

        # Also try getting the input value
        input_el = container.first.locator("input")
        if input_el.count() > 0:
            for i in range(input_el.count()):
                val = input_el.nth(i).input_value()
                if "System.String" in val:
                    has_default = True
                    break

        assert has_default, \
            f"TypeName should default to System.String. Text: {container_text[:200]}"


class TestTestCompileAction:
    """Tests for the Test Compile All action on CustomClass ListView."""

    def test_08_test_compile_success(self, page):
        """Verify Test Compile All on the ListView shows success for all runtime classes."""
        nav, lv = nav_to_custom_class(page)

        # Ensure at least one runtime class exists (ValidTestClass from test_04)
        if not lv.has_row_with_text("ValidTestClass"):
            lv.click_new()
            page.wait_for_timeout(1000)
            detail = DetailViewPage(page)
            detail.fill_field("Class Name", "ValidTestClass")
            detail.fill_field("Navigation Group", "Test")
            detail.click_save()
            page.wait_for_timeout(2000)
            nav.navigate_to("Schema Management", "Custom Class")
            lv.wait_for_grid()

        page.wait_for_timeout(1000)

        # Click the Test Compile All action on the ListView
        compile_btn = page.locator('dxbl-toolbar-item[text="Test Compile All"]')
        assert compile_btn.count() > 0, "Test Compile All action should be visible on CustomClass ListView"

        compile_btn.first.click()
        page.wait_for_timeout(3000)

        # Check for success message
        body_text = page.locator("body").inner_text()
        assert "successful" in body_text.lower() or "success" in body_text.lower() or "compiled" in body_text.lower(), \
            f"Should show compilation success message. Page text: {body_text[:500]}"


class TestCleanup:
    """Clean up all Phase 3 test data."""

    def test_99_cleanup(self, page):
        """Remove all test data created during Phase 3 tests."""
        nav, lv = nav_to_custom_class(page)
        for name in ["ValidTestClass", "123Invalid", "class", "BaseObject"]:
            delete_if_exists(page, name)
