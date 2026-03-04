"""Phase 5 Tests: Entity Relationships — runtime entities can reference other entities.

Tests verify that creating CustomField with TypeName='Reference' and ReferencedClassName
generates FK properties, navigation properties, and real PostgreSQL FK constraints.
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
    """Get a PostgreSQL connection."""
    import psycopg2
    return psycopg2.connect(
        host=DB_HOST, port=DB_PORT, dbname=DB_NAME,
        user=DB_USER, password=DB_PASS
    )


def wait_for_server(timeout=60):
    """Poll until the server responds (handles restart window)."""
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
    """Full page reload + wait for XAF navigation, tolerating brief downtime."""
    wait_for_server(timeout=30)
    page.goto(BASE_URL, wait_until="networkidle", timeout=60000)
    page.wait_for_selector(".xaf-nav-link", timeout=60000)
    page.wait_for_timeout(3000)


def wait_for_deploy_restart(page):
    """Wait for Deploy Schema + server restart cycle.

    The server exits with code 42 and a wrapper script restarts it as a fresh process.
    This takes longer than an in-process restart, so we use generous timeouts.
    """
    # Wait for deploy action to process and trigger restart
    page.wait_for_timeout(5000)
    # Server will be briefly down during process restart
    import time
    time.sleep(5)
    wait_for_server(timeout=60)
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
    """Click the 'Deploy Schema' action button on CustomClass ListView."""
    deploy_btn = page.locator('dxbl-toolbar-item[text="Deploy Schema"]')
    if deploy_btn.count() == 0:
        deploy_btn = page.locator('button:has-text("Deploy Schema"), span:has-text("Deploy Schema")')
    deploy_btn.first.click()
    page.wait_for_timeout(1000)
    confirm_btn = page.locator('button:has-text("Yes"), button:has-text("OK")')
    if confirm_btn.count() > 0:
        confirm_btn.first.click()


def delete_if_exists(page, text):
    lv = ListViewPage(page)
    if lv.has_row_with_text(text):
        lv.select_row_with_text(text)
        lv.click_delete()
        lv.confirm_delete()
        page.wait_for_timeout(500)


def create_class_via_ui(page, class_name, nav_group, description=""):
    """Create a CustomClass via the UI."""
    nav, lv = nav_to_custom_class(page)
    delete_if_exists(page, class_name)

    lv.click_new()
    page.wait_for_timeout(2000)
    detail = DetailViewPage(page)
    detail.fill_field("Class Name", class_name)
    detail.fill_field("Navigation Group", nav_group)
    if description:
        detail.fill_field("Description", description)
    detail.click_save()
    page.wait_for_timeout(2000)

    # Navigate back to list view to reset page state
    nav = NavigationPage(page)
    nav.navigate_to("Schema Management", "Custom Class")
    lv = ListViewPage(page)
    lv.wait_for_grid()


def insert_field_via_db(class_name, field_name, type_name="System.String",
                        referenced_class_name=None, is_default=False):
    """Insert a CustomField directly via PostgreSQL."""
    conn = get_db_connection()
    try:
        cur = conn.cursor()
        # Find the CustomClass ID
        cur.execute(
            'SELECT "ID" FROM "CustomClasses" WHERE "ClassName" = %s AND ("GCRecord" IS NULL OR "GCRecord" = 0)',
            (class_name,)
        )
        row = cur.fetchone()
        if not row:
            raise ValueError(f"CustomClass '{class_name}' not found")
        class_id = row[0]

        # Delete existing field with same name
        cur.execute(
            'DELETE FROM "CustomFields" WHERE "CustomClassId" = %s AND "FieldName" = %s',
            (class_id, field_name)
        )

        # Insert the field
        cur.execute(
            '''INSERT INTO "CustomFields" ("ID", "CustomClassId", "FieldName", "TypeName",
               "IsRequired", "IsDefaultField", "Description", "ReferencedClassName",
               "SortOrder", "GCRecord", "OptimisticLockField")
               VALUES (gen_random_uuid(), %s, %s, %s, false, %s, NULL, %s, 0, 0, 0)''',
            (class_id, field_name, type_name, is_default, referenced_class_name)
        )
        conn.commit()
    finally:
        conn.close()


def cleanup_db_classes(*class_names):
    """Delete CustomClass records and their fields from DB."""
    conn = get_db_connection()
    try:
        cur = conn.cursor()
        for name in class_names:
            cur.execute(
                'DELETE FROM "CustomClasses" WHERE "ClassName" = %s',
                (name,)
            )
        conn.commit()
    finally:
        conn.close()


class TestRelationshipSetup:
    """Create Department and Employee classes with a reference relationship."""

    def test_01_create_department_class(self, page):
        """Create RelDepartment class."""
        create_class_via_ui(page, "RelDepartment", "Organization", "Department for relationship test")

    def test_02_create_employee_class(self, page):
        """Create RelEmployee class."""
        create_class_via_ui(page, "RelEmployee", "Organization", "Employee for relationship test")

    def test_03_add_fields_via_db(self, page):
        """Add fields including a reference field via direct DB insert."""
        insert_field_via_db("RelDepartment", "DeptName", "System.String", is_default=True)
        insert_field_via_db("RelEmployee", "EmpName", "System.String", is_default=True)
        insert_field_via_db("RelEmployee", "Department", "Reference",
                            referenced_class_name="RelDepartment")

        # Verify via Custom Field list
        nav = NavigationPage(page)
        nav.navigate_to("Schema Management", "Custom Field")
        lv = ListViewPage(page)
        lv.wait_for_grid()
        page.wait_for_timeout(500)
        assert lv.has_row_with_text("DeptName"), "DeptName field should exist"
        assert lv.has_row_with_text("EmpName"), "EmpName field should exist"
        assert lv.has_row_with_text("Department"), "Department reference field should exist"

    def test_04_deploy_and_restart(self, page):
        """Deploy schema changes and wait for server restart."""
        nav, lv = nav_to_custom_class(page)
        assert lv.has_row_with_text("RelDepartment"), "RelDepartment should be in list"
        assert lv.has_row_with_text("RelEmployee"), "RelEmployee should be in list"

        click_deploy_schema(page)
        wait_for_deploy_restart(page)

        # Verify Organization nav group exists
        links = page.locator(".xaf-nav-link").all_text_contents()
        assert "Organization" in links, f"Organization nav group should exist. Links: {links}"


class TestRelationshipFunctionality:
    """Verify FK relationship works in the deployed runtime entities."""

    def test_05_create_department_record(self, page):
        """Create a Department record to reference."""
        reload_and_wait(page)

        # Use direct URL navigation for runtime entity views after restart
        # (JS click on nav links doesn't trigger Blazor client-side routing)
        page.goto(f"{BASE_URL}/RelDepartment_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(3000)
        lv = ListViewPage(page)
        lv.wait_for_grid()

        lv.click_new()
        page.wait_for_timeout(2000)
        detail = DetailViewPage(page)
        detail.fill_field("Dept Name", "Engineering")
        detail.click_save()
        page.wait_for_timeout(2000)

        page.goto(f"{BASE_URL}/RelDepartment_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(2000)
        lv.wait_for_grid()
        page.wait_for_timeout(500)
        assert lv.has_row_with_text("Engineering"), "Engineering department should exist"

    def test_06_create_employee_with_reference(self, page):
        """Create an Employee referencing the Engineering department."""
        reload_and_wait(page)

        # Use direct URL navigation for runtime entity views after restart
        page.goto(f"{BASE_URL}/RelEmployee_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(3000)
        lv = ListViewPage(page)
        lv.wait_for_grid()

        lv.click_new()
        page.wait_for_timeout(2000)
        detail = DetailViewPage(page)
        detail.fill_field("Emp Name", "Alice")
        # The Department field is a lookup — fill_field types text and tabs
        # XAF's lookup editor should match "Engineering" from the dropdown
        detail.fill_field("Department", "Engineering")
        page.wait_for_timeout(1000)
        detail.click_save()
        page.wait_for_timeout(2000)

        page.goto(f"{BASE_URL}/RelEmployee_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(2000)
        lv.wait_for_grid()
        page.wait_for_timeout(500)
        assert lv.has_row_with_text("Alice"), "Alice employee should exist"

    def test_07_fk_constraint_exists(self, page):
        """Verify the FK constraint was created in PostgreSQL."""
        conn = get_db_connection()
        try:
            cur = conn.cursor()
            cur.execute("""
                SELECT constraint_name FROM information_schema.table_constraints
                WHERE table_name = 'RelEmployee'
                AND constraint_type = 'FOREIGN KEY'
                AND constraint_schema = 'public'
            """)
            constraints = [row[0] for row in cur.fetchall()]
            assert any("Department" in c for c in constraints), \
                f"FK constraint for Department should exist. Found: {constraints}"
        finally:
            conn.close()


class TestCleanup:
    """Clean up all Phase 5 test data."""

    def test_99_cleanup(self, page):
        """Remove test entities."""
        reload_and_wait(page)

        # Delete Employee records first (FK dependency)
        try:
            page.goto(f"{BASE_URL}/RelEmployee_ListView", wait_until="networkidle", timeout=60000)
            page.wait_for_timeout(2000)
            lv = ListViewPage(page)
            lv.wait_for_grid()
            page.wait_for_timeout(500)
            for name in ["Alice"]:
                if lv.has_row_with_text(name):
                    lv.select_row_with_text(name)
                    lv.click_delete()
                    lv.confirm_delete()
                    page.wait_for_timeout(500)
        except Exception:
            pass

        # Delete Department records
        try:
            page.goto(f"{BASE_URL}/RelDepartment_ListView", wait_until="networkidle", timeout=60000)
            page.wait_for_timeout(2000)
            lv = ListViewPage(page)
            lv.wait_for_grid()
            page.wait_for_timeout(500)
            for name in ["Engineering"]:
                if lv.has_row_with_text(name):
                    lv.select_row_with_text(name)
                    lv.click_delete()
                    lv.confirm_delete()
                    page.wait_for_timeout(500)
        except Exception:
            pass

        # Delete metadata classes (cascade deletes fields)
        nav = NavigationPage(page)
        nav.navigate_to("Schema Management", "Custom Class")
        lv = ListViewPage(page)
        lv.wait_for_grid()
        for name in ["RelEmployee", "RelDepartment"]:
            delete_if_exists(page, name)
