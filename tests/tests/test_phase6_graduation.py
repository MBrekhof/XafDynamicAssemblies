"""Phase 6 Tests: Graduation — runtime entities exported to compiled C# source.

Tests verify that the Graduate action generates production-quality C# source,
changes status to Compiled, and the entity is removed from runtime compilation
after deploy while preserving the database table and data.
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


def wait_for_deploy_restart(page):
    """Wait for Deploy Schema + server restart cycle (process-level restart)."""
    page.wait_for_timeout(5000)
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


class TestGraduationSetup:
    """Create a GradTest class, add fields, deploy, and add data."""

    def test_01_create_gradtest_class(self, page):
        """Create GradTest class for graduation testing."""
        create_class_via_ui(page, "GradTest", "GradGroup", "Test entity for graduation")

    def test_02_add_gradtest_fields(self, page):
        """Add fields to GradTest class via DB."""
        insert_field_via_db("GradTest", "Title", "System.String", is_default=True)
        insert_field_via_db("GradTest", "Amount", "System.Decimal")

        nav = NavigationPage(page)
        nav.navigate_to("Schema Management", "Custom Field")
        lv = ListViewPage(page)
        lv.wait_for_grid()
        page.wait_for_timeout(500)
        assert lv.has_row_with_text("Title"), "Title field should exist"

    def test_03_deploy_gradtest(self, page):
        """Deploy schema so GradTest becomes a runtime entity."""
        nav, lv = nav_to_custom_class(page)
        assert lv.has_row_with_text("GradTest"), "GradTest should be in list"
        click_deploy_schema(page)
        wait_for_deploy_restart(page)

        links = page.locator(".xaf-nav-link").all_text_contents()
        assert "GradGroup" in links, f"GradGroup nav should exist after deploy. Links: {links}"

    def test_04_create_gradtest_data(self, page):
        """Create a record in GradTest to verify data preservation after graduation."""
        reload_and_wait(page)
        page.goto(f"{BASE_URL}/GradTest_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(3000)
        lv = ListViewPage(page)
        lv.wait_for_grid()

        lv.click_new()
        page.wait_for_timeout(2000)
        detail = DetailViewPage(page)
        detail.fill_field("Title", "GradTestRecord1")
        detail.click_save()
        page.wait_for_timeout(2000)

        page.goto(f"{BASE_URL}/GradTest_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(2000)
        lv.wait_for_grid()
        assert lv.has_row_with_text("GradTestRecord1"), "GradTestRecord1 should exist"


class TestGraduateAction:
    """Test the Graduate action on CustomClass."""

    def test_05_graduate_action_available(self, page):
        """Verify the Graduate action is available on CustomClass DetailView."""
        nav, lv = nav_to_custom_class(page)
        lv.double_click_row_with_text("GradTest")
        page.wait_for_timeout(2000)

        # Look for the Graduate action button
        graduate_btn = page.locator('dxbl-toolbar-item[text="Graduate"]')
        if graduate_btn.count() == 0:
            graduate_btn = page.locator('button:has-text("Graduate"), span:has-text("Graduate")')
        assert graduate_btn.count() > 0, "Graduate action should be available"

    def test_06_graduate_generates_source(self, page):
        """Click Graduate and verify source code is generated and status changes."""
        nav, lv = nav_to_custom_class(page)
        lv.double_click_row_with_text("GradTest")
        page.wait_for_timeout(2000)

        # Click Graduate
        graduate_btn = page.locator('dxbl-toolbar-item[text="Graduate"]')
        if graduate_btn.count() == 0:
            graduate_btn = page.locator('button:has-text("Graduate"), span:has-text("Graduate")')
        graduate_btn.first.click()
        page.wait_for_timeout(1000)

        # Accept confirmation dialog
        confirm_btn = page.locator('button:has-text("Yes"), button:has-text("OK")')
        if confirm_btn.count() > 0:
            confirm_btn.first.click()
        page.wait_for_timeout(2000)

        # Dismiss success message if shown
        ok_btn = page.locator('button:has-text("OK")')
        if ok_btn.count() > 0:
            ok_btn.first.click()
            page.wait_for_timeout(500)

        # Verify status changed to Compiled
        detail = DetailViewPage(page)
        status_value = detail.get_field_value("Status")
        assert status_value == "Compiled" or "Compiled" in str(status_value), \
            f"Status should be Compiled, got: {status_value}"

        # Verify graduated source was generated (check via DB since the field might
        # be rendered as a large text area)
        conn = get_db_connection()
        try:
            cur = conn.cursor()
            cur.execute(
                'SELECT "GraduatedSource", "Status" FROM "CustomClasses" WHERE "ClassName" = %s',
                ("GradTest",)
            )
            row = cur.fetchone()
            assert row is not None, "GradTest should exist in DB"
            source = row[0]
            status = row[1]
            assert source is not None and len(source) > 0, "GraduatedSource should be populated"
            assert "class GradTest" in source, "Source should contain class definition"
            assert "BaseObject" in source, "Source should inherit from BaseObject"
            assert "Title" in source, "Source should contain Title property"
            assert "Amount" in source, "Source should contain Amount property"
            assert "DbContext" in source, "Source should contain DbContext registration hint"
            assert "migration" in source.lower(), "Source should contain migration note"
            assert status == "Compiled", f"DB status should be Compiled, got: {status}"
        finally:
            conn.close()


class TestGraduationRemovesFromRuntime:
    """Verify that after graduation + deploy, the entity is removed from runtime."""

    def test_07_deploy_after_graduation(self, page):
        """Deploy schema after graduation — GradTest should be removed from runtime nav."""
        nav, lv = nav_to_custom_class(page)
        click_deploy_schema(page)
        wait_for_deploy_restart(page)

        # GradGroup nav should NOT exist (GradTest was the only class in that group)
        links = page.locator(".xaf-nav-link").all_text_contents()
        assert "GradGroup" not in links, \
            f"GradGroup should be removed from nav after graduation. Links: {links}"

    def test_08_data_preserved_in_database(self, page):
        """Verify the GradTest table and data still exist in PostgreSQL."""
        conn = get_db_connection()
        try:
            cur = conn.cursor()
            # Check table exists
            cur.execute("""
                SELECT EXISTS (
                    SELECT FROM information_schema.tables
                    WHERE table_schema = 'public' AND table_name = 'GradTest'
                )
            """)
            assert cur.fetchone()[0], "GradTest table should still exist in PostgreSQL"

            # Check data preserved
            cur.execute('SELECT "Title" FROM "GradTest"')
            rows = cur.fetchall()
            titles = [r[0] for r in rows]
            assert "GradTestRecord1" in titles, \
                f"GradTestRecord1 should still exist in DB. Found: {titles}"
        finally:
            conn.close()


class TestCleanup:
    """Clean up Phase 6 test data."""

    def test_99_cleanup(self, page):
        """Remove test entities."""
        # Delete GradTest class metadata
        nav = NavigationPage(page)
        nav.navigate_to("Schema Management", "Custom Class")
        lv = ListViewPage(page)
        lv.wait_for_grid()
        delete_if_exists(page, "GradTest")

        # Drop the GradTest table
        conn = get_db_connection()
        try:
            cur = conn.cursor()
            cur.execute('DROP TABLE IF EXISTS "GradTest" CASCADE')
            conn.commit()
        finally:
            conn.close()
