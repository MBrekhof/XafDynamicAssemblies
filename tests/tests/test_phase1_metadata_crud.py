"""Phase 1 Tests: CustomClass and CustomField CRUD operations.

Tests are ordered and run sequentially. Each test gets a fresh browser context
but the database persists across tests.
"""
import pytest
import sys
import os

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from pages.navigation_page import NavigationPage
from pages.list_view_page import ListViewPage
from pages.detail_view_page import DetailViewPage


def nav_to_custom_class(page):
    """Navigate to Custom Class ListView and wait for grid."""
    nav = NavigationPage(page)
    nav.navigate_to("Schema Management", "Custom Class")
    lv = ListViewPage(page)
    lv.wait_for_grid()
    return nav, lv


def nav_to_custom_field(page):
    """Navigate to Custom Field ListView and wait for grid."""
    nav = NavigationPage(page)
    nav.navigate_to("Schema Management", "Custom Field")
    lv = ListViewPage(page)
    lv.wait_for_grid()
    return nav, lv


def create_custom_class(page, class_name, nav_group="", description=""):
    """Helper: create a CustomClass and return to the list view."""
    nav, lv = nav_to_custom_class(page)
    lv.click_new()
    detail = DetailViewPage(page)
    detail.fill_field("Class Name", class_name)
    if nav_group:
        detail.fill_field("Navigation Group", nav_group)
    if description:
        detail.fill_field("Description", description)
    detail.click_save()
    page.wait_for_timeout(2000)
    # Navigate back to list
    nav.navigate_to("Schema Management", "Custom Class")
    lv.wait_for_grid()
    page.wait_for_timeout(1000)
    return nav, lv


def delete_if_exists(page, text):
    """Delete a row from the current grid if it exists."""
    lv = ListViewPage(page)
    if lv.has_row_with_text(text):
        lv.select_row_with_text(text)
        lv.click_delete()
        lv.confirm_delete()
        page.wait_for_timeout(500)


class TestCustomClassCRUD:
    """Tests for CustomClass create, read, update, delete."""

    def test_01_navigate_to_custom_class(self, page):
        """Verify Custom Class ListView loads under Schema Management."""
        nav, lv = nav_to_custom_class(page)
        assert page.locator(".dxbl-grid").count() > 0

    def test_02_create_custom_class(self, page):
        """Create a new CustomClass and verify it appears in the list."""
        nav, lv = nav_to_custom_class(page)
        # Clean up if leftover from previous run
        delete_if_exists(page, "CrudTestClass")

        nav, lv = create_custom_class(page, "CrudTestClass", "TestGroup", "Test description")
        assert lv.has_row_with_text("CrudTestClass")

    def test_03_read_custom_class(self, page):
        """Open an existing CustomClass and verify field values."""
        nav, lv = nav_to_custom_class(page)
        lv.double_click_row_with_text("CrudTestClass")

        detail = DetailViewPage(page)
        assert detail.get_field_value("Class Name") == "CrudTestClass"
        assert detail.get_field_value("Navigation Group") == "TestGroup"
        assert "Test description" in detail.get_field_value("Description")

    def test_04_status_defaults_to_runtime(self, page):
        """Verify new CustomClass has Status = Runtime by default."""
        nav, lv = nav_to_custom_class(page)
        lv.double_click_row_with_text("CrudTestClass")

        detail = DetailViewPage(page)
        status_text = detail.get_field_text("Status")
        assert "Runtime" in status_text

    def test_05_edit_custom_class(self, page):
        """Edit a CustomClass description and verify the change persists."""
        nav, lv = nav_to_custom_class(page)
        lv.double_click_row_with_text("CrudTestClass")

        detail = DetailViewPage(page)
        detail.fill_field("Description", "Updated description")
        detail.click_save()
        page.wait_for_timeout(500)

        # Navigate back and reopen
        nav.navigate_to("Schema Management", "Custom Class")
        lv.wait_for_grid()
        lv.double_click_row_with_text("CrudTestClass")
        assert "Updated description" in detail.get_field_value("Description")

    def test_06_create_second_custom_class(self, page):
        """Create a second CustomClass to verify multiple classes coexist."""
        nav, lv = nav_to_custom_class(page)
        delete_if_exists(page, "CrudTestClass2")

        nav, lv = create_custom_class(page, "CrudTestClass2", "HR")
        assert lv.has_row_with_text("CrudTestClass2")
        assert lv.has_row_with_text("CrudTestClass")

    def test_07_delete_custom_class(self, page):
        """Delete a CustomClass and verify removal."""
        nav, lv = nav_to_custom_class(page)
        delete_if_exists(page, "CrudTestClass2")
        assert not lv.has_row_with_text("CrudTestClass2")


class TestCustomFieldCRUD:
    """Tests for CustomField CRUD via the standalone list."""

    def test_08_navigate_to_custom_field(self, page):
        """Verify Custom Field ListView loads."""
        nav, lv = nav_to_custom_field(page)
        assert page.locator(".dxbl-grid").count() > 0

    def test_09_create_custom_field(self, page):
        """Create a CustomField and verify it appears."""
        nav, lv = nav_to_custom_field(page)
        delete_if_exists(page, "TestFieldName")

        lv.click_new()
        detail = DetailViewPage(page)
        detail.fill_field("Field Name", "TestFieldName")
        detail.fill_field("Description", "A test field")
        detail.click_save()
        page.wait_for_timeout(1000)

        nav.navigate_to("Schema Management", "Custom Field")
        lv.wait_for_grid()
        page.wait_for_timeout(500)
        assert lv.has_row_with_text("TestFieldName")

    def test_10_delete_custom_field(self, page):
        """Delete a CustomField and verify removal."""
        nav, lv = nav_to_custom_field(page)
        delete_if_exists(page, "TestFieldName")
        assert not lv.has_row_with_text("TestFieldName")


class TestCleanup:
    """Clean up all test data at the end."""

    def test_99_cleanup(self, page):
        """Remove all test data created during tests."""
        nav, lv = nav_to_custom_class(page)
        for name in ["CrudTestClass", "CrudTestClass2", "TestProduct", "TestProduct2"]:
            delete_if_exists(page, name)
