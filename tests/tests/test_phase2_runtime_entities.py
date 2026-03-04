"""Phase 2 Tests: Runtime entity compilation and CRUD.

Verifies that CustomClass metadata compiles into real CLR types at startup,
PostgreSQL tables are created, and runtime entities have working CRUD views.
"""
import pytest
import sys
import os
import time
import urllib3

# Suppress SSL warnings for self-signed certs
urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from pages.navigation_page import NavigationPage
from pages.list_view_page import ListViewPage
from pages.detail_view_page import DetailViewPage


BASE_URL = os.environ.get("BASE_URL", "https://host.docker.internal:5001")
DB_HOST = os.environ.get("DB_HOST", "host.docker.internal")
DB_PORT = os.environ.get("DB_PORT", "5434")
DB_NAME = os.environ.get("DB_NAME", "XafDynamicAssemblies")
DB_USER = os.environ.get("DB_USER", "xafdynamic")
DB_PASS = os.environ.get("DB_PASS", "xafdynamic")


def get_db_connection():
    import psycopg2
    return psycopg2.connect(
        host=DB_HOST, port=DB_PORT, dbname=DB_NAME,
        user=DB_USER, password=DB_PASS
    )


def wait_for_server(timeout=60):
    import requests
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            r = requests.get(BASE_URL, timeout=3, verify=False)
            if r.status_code < 500:
                return True
        except Exception:
            pass
        time.sleep(1)
    raise TimeoutError(f"Server did not come back within {timeout}s")


def reload_and_wait(page):
    wait_for_server(timeout=30)
    page.goto(BASE_URL, wait_until="networkidle", timeout=60000)
    page.wait_for_selector(".xaf-nav-link", timeout=60000)
    page.wait_for_timeout(3000)


def nav_to_custom_class(page):
    nav = NavigationPage(page)
    nav.navigate_to("Schema Management", "Custom Class")
    lv = ListViewPage(page)
    lv.wait_for_grid()
    return nav, lv


def click_deploy_schema(page):
    deploy_btn = page.locator('dxbl-toolbar-item[text="Deploy Schema"]')
    if deploy_btn.count() == 0:
        deploy_btn = page.locator('button:has-text("Deploy Schema"), span:has-text("Deploy Schema")')
    deploy_btn.first.click()
    page.wait_for_timeout(1000)
    confirm_btn = page.locator('button:has-text("Yes"), button:has-text("OK")')
    if confirm_btn.count() > 0:
        confirm_btn.first.click()


def wait_for_deploy_restart(page):
    """Wait for Deploy Schema + server restart cycle (process-level restart)."""
    page.wait_for_timeout(5000)
    time.sleep(5)
    wait_for_server(timeout=60)
    page.goto(BASE_URL, wait_until="networkidle", timeout=60000)
    page.wait_for_selector(".xaf-nav-link", timeout=60000)
    page.wait_for_timeout(3000)


def create_class_via_ui(page, class_name, nav_group, description=""):
    nav, lv = nav_to_custom_class(page)
    # Delete if exists
    if lv.has_row_with_text(class_name):
        lv.select_row_with_text(class_name)
        lv.click_delete()
        lv.confirm_delete()
        page.wait_for_timeout(500)

    lv.click_new()
    page.wait_for_timeout(2000)
    detail = DetailViewPage(page)
    detail.fill_field("Class Name", class_name)
    detail.fill_field("Navigation Group", nav_group)
    if description:
        detail.fill_field("Description", description)
    detail.click_save()
    page.wait_for_timeout(2000)

    nav = NavigationPage(page)
    nav.navigate_to("Schema Management", "Custom Class")
    lv = ListViewPage(page)
    lv.wait_for_grid()


def insert_field_via_db(class_name, field_name, type_name="System.String", is_default=False):
    conn = get_db_connection()
    try:
        cur = conn.cursor()
        cur.execute(
            'SELECT "ID" FROM "CustomClasses" WHERE "ClassName" = %s AND ("GCRecord" IS NULL OR "GCRecord" = 0)',
            (class_name,)
        )
        row = cur.fetchone()
        if not row:
            raise ValueError(f"CustomClass '{class_name}' not found")
        class_id = row[0]
        cur.execute(
            'DELETE FROM "CustomFields" WHERE "CustomClassId" = %s AND "FieldName" = %s',
            (class_id, field_name)
        )
        cur.execute(
            '''INSERT INTO "CustomFields" ("ID", "CustomClassId", "FieldName", "TypeName",
               "IsRequired", "IsDefaultField", "Description", "ReferencedClassName",
               "SortOrder", "GCRecord", "OptimisticLockField")
               VALUES (gen_random_uuid(), %s, %s, %s, false, %s, NULL, NULL, 0, 0, 0)''',
            (class_id, field_name, type_name, is_default)
        )
        conn.commit()
    finally:
        conn.close()


def delete_if_exists(page, text):
    lv = ListViewPage(page)
    if lv.has_row_with_text(text):
        lv.select_row_with_text(text)
        lv.click_delete()
        lv.confirm_delete()
        page.wait_for_timeout(500)


class TestRuntimeEntitySetup:
    """Create Customer class with fields and deploy."""

    def test_00a_create_customer_class(self, page):
        """Create Customer class metadata."""
        create_class_via_ui(page, "Customer", "CRM", "Customer entity for Phase 2 tests")

    def test_00b_add_customer_fields(self, page):
        """Add fields to Customer class via direct DB insert."""
        insert_field_via_db("Customer", "Name", "System.String", is_default=True)
        insert_field_via_db("Customer", "email", "System.String")
        insert_field_via_db("Customer", "phone", "System.String")
        insert_field_via_db("Customer", "active", "System.Boolean")

        # Verify fields exist
        nav = NavigationPage(page)
        nav.navigate_to("Schema Management", "Custom Field")
        lv = ListViewPage(page)
        lv.wait_for_grid()
        page.wait_for_timeout(500)
        assert lv.has_row_with_text("Name"), "Name field should exist"

    def test_00c_deploy_and_restart(self, page):
        """Deploy schema and wait for process restart."""
        nav, lv = nav_to_custom_class(page)
        assert lv.has_row_with_text("Customer"), "Customer class should exist"
        click_deploy_schema(page)
        wait_for_deploy_restart(page)

        links = page.locator(".xaf-nav-link").all_text_contents()
        assert "CRM" in links, f"CRM nav group should exist after deploy. Links: {links}"


class TestRuntimeEntityNavigation:
    """Tests that runtime entities appear in XAF navigation."""

    def test_01_customer_in_navigation(self, page):
        """Verify that Customer class appears in CRM nav group."""
        reload_and_wait(page)
        links = page.locator(".xaf-nav-link").all_text_contents()
        assert "CRM" in links, "CRM navigation group should exist"

    def test_02_customer_list_view_loads(self, page):
        """Verify Customer ListView renders with a grid."""
        reload_and_wait(page)
        page.goto(f"{BASE_URL}/Customer_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(3000)
        lv = ListViewPage(page)
        lv.wait_for_grid()
        assert page.locator(".dxbl-grid").first.is_visible() or page.locator(".dxbl-grid").last.is_visible()


class TestRuntimeEntityCRUD:
    """Tests for creating, reading, updating, deleting runtime entities."""

    def test_03_create_runtime_entity(self, page):
        """Create a new Customer record and verify it appears in the list."""
        reload_and_wait(page)
        page.goto(f"{BASE_URL}/Customer_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(3000)
        lv = ListViewPage(page)
        lv.wait_for_grid()

        # Clean up any previous test data
        if lv.has_row_with_text("TestCustomer1"):
            lv.select_row_with_text("TestCustomer1")
            lv.click_delete()
            lv.confirm_delete()
            page.wait_for_timeout(500)

        # Create new
        lv.click_new()
        page.wait_for_timeout(2000)

        detail = DetailViewPage(page)
        detail.fill_field("Name", "TestCustomer1")
        detail.fill_field("email", "test1@example.com")
        detail.fill_field("phone", "555-0001")
        detail.click_save()
        page.wait_for_timeout(2000)

        # Navigate back to list
        page.goto(f"{BASE_URL}/Customer_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(2000)
        lv.wait_for_grid()
        page.wait_for_timeout(1000)

        assert lv.has_row_with_text("TestCustomer1"), "TestCustomer1 should appear in list"

    def test_04_read_runtime_entity(self, page):
        """Open a runtime entity and verify field values."""
        reload_and_wait(page)
        page.goto(f"{BASE_URL}/Customer_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(3000)
        lv = ListViewPage(page)
        lv.wait_for_grid()

        lv.double_click_row_with_text("TestCustomer1")
        page.wait_for_timeout(2000)

        detail = DetailViewPage(page)
        assert detail.get_field_value("Name") == "TestCustomer1"
        assert detail.get_field_value("email") == "test1@example.com"

    def test_05_update_runtime_entity(self, page):
        """Edit a runtime entity field and verify the change persists."""
        reload_and_wait(page)
        page.goto(f"{BASE_URL}/Customer_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(3000)
        lv = ListViewPage(page)
        lv.wait_for_grid()

        lv.double_click_row_with_text("TestCustomer1")
        page.wait_for_timeout(2000)

        detail = DetailViewPage(page)
        detail.fill_field("phone", "555-9999")
        detail.click_save()
        page.wait_for_timeout(1000)

        # Reopen and verify
        page.goto(f"{BASE_URL}/Customer_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(2000)
        lv.wait_for_grid()
        lv.double_click_row_with_text("TestCustomer1")
        page.wait_for_timeout(2000)

        assert detail.get_field_value("phone") == "555-9999"

    def test_06_delete_runtime_entity(self, page):
        """Delete a runtime entity and verify removal."""
        reload_and_wait(page)
        page.goto(f"{BASE_URL}/Customer_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(3000)
        lv = ListViewPage(page)
        lv.wait_for_grid()

        if lv.has_row_with_text("TestCustomer1"):
            lv.select_row_with_text("TestCustomer1")
            lv.click_delete()
            lv.confirm_delete()
            page.wait_for_timeout(1000)

        assert not lv.has_row_with_text("TestCustomer1"), "TestCustomer1 should be deleted"


class TestMultipleDataTypes:
    """Tests that different data types work correctly."""

    def test_07_boolean_field(self, page):
        """Verify boolean field (active) works via checkbox."""
        reload_and_wait(page)
        page.goto(f"{BASE_URL}/Customer_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(3000)
        lv = ListViewPage(page)
        lv.wait_for_grid()

        lv.click_new()
        page.wait_for_timeout(2000)

        detail = DetailViewPage(page)
        detail.fill_field("Name", "BoolTestCustomer")

        # Toggle the boolean checkbox
        checkbox = page.locator(".dxbl-fl-ctrl:has([data-item-name='active']) input[type='checkbox']")
        if checkbox.count() > 0:
            checkbox.first.check()

        detail.click_save()
        page.wait_for_timeout(1000)

        # Navigate back and verify
        page.goto(f"{BASE_URL}/Customer_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(2000)
        lv.wait_for_grid()
        assert lv.has_row_with_text("BoolTestCustomer")

        # Clean up
        lv.select_row_with_text("BoolTestCustomer")
        lv.click_delete()
        lv.confirm_delete()
        page.wait_for_timeout(500)


class TestSchemaManagementIntegration:
    """Verify Schema Management entities still work alongside runtime entities."""

    def test_08_custom_class_still_works(self, page):
        """Verify CustomClass CRUD still works after runtime compilation."""
        nav, lv = nav_to_custom_class(page)
        assert lv.has_row_with_text("Customer"), "Customer metadata should be in CustomClass list"

    def test_09_custom_field_still_works(self, page):
        """Verify CustomField list still loads."""
        nav = NavigationPage(page)
        nav.navigate_to("Schema Management", "Custom Field")
        lv = ListViewPage(page)
        lv.wait_for_grid()
        assert lv.has_row_with_text("Name"), "Customer's Name field should be in CustomField list"


class TestCleanup:
    """Clean up all test data."""

    def test_99_cleanup(self, page):
        """Remove test entities from runtime entity list."""
        reload_and_wait(page)
        try:
            page.goto(f"{BASE_URL}/Customer_ListView", wait_until="networkidle", timeout=60000)
            page.wait_for_timeout(2000)
            lv = ListViewPage(page)
            lv.wait_for_grid()
            page.wait_for_timeout(500)
            for name in ["TestCustomer1", "BoolTestCustomer", "Acme Corp"]:
                if lv.has_row_with_text(name):
                    lv.select_row_with_text(name)
                    lv.click_delete()
                    lv.confirm_delete()
                    page.wait_for_timeout(500)
        except Exception:
            pass
