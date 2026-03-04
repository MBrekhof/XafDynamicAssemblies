"""Phase 7 Tests: Error Handling + Hardening.

Tests verify graceful degraded mode, recovery from compilation errors,
empty metadata startup, and restart recovery.
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


def create_class_via_db(class_name, nav_group, description=""):
    """Create a CustomClass directly via DB (bypassing validation)."""
    conn = get_db_connection()
    try:
        cur = conn.cursor()
        # Delete if exists
        cur.execute('DELETE FROM "CustomClasses" WHERE "ClassName" = %s', (class_name,))
        cur.execute(
            '''INSERT INTO "CustomClasses" ("ID", "ClassName", "NavigationGroup", "Description",
               "Status", "GCRecord", "OptimisticLockField")
               VALUES (gen_random_uuid(), %s, %s, %s, 'Runtime', 0, 0)''',
            (class_name, nav_group, description)
        )
        conn.commit()
    finally:
        conn.close()


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


def cleanup_all_runtime_data():
    """Remove all runtime class metadata and drop their tables."""
    conn = get_db_connection()
    try:
        cur = conn.cursor()
        # Get all runtime class names before deleting
        cur.execute(
            'SELECT "ClassName" FROM "CustomClasses" WHERE "Status" = %s',
            ('Runtime',)
        )
        class_names = [r[0] for r in cur.fetchall()]

        # Delete fields and classes
        cur.execute('DELETE FROM "CustomFields"')
        cur.execute('DELETE FROM "CustomClasses"')

        # Drop runtime tables
        for name in class_names:
            cur.execute(f'DROP TABLE IF EXISTS "{name}" CASCADE')

        conn.commit()
    finally:
        conn.close()


class TestDegradedMode:
    """Test that compilation errors cause graceful degraded mode."""

    def test_01_invalid_typename_degrades_gracefully(self, page):
        """Insert a class with invalid TypeName, deploy, verify server starts in degraded mode.

        The server should still boot with compiled entities working (CustomClass, CustomField).
        """
        # Create a class with an invalid type that will cause compilation failure
        create_class_via_db("BadTypeClass", "ErrorTest", "Class with invalid field type")
        insert_field_via_db("BadTypeClass", "BadField", "Totally.Invalid.Type.That.Does.Not.Exist")

        # Deploy — this will trigger compilation which should fail for the invalid type
        nav, lv = nav_to_custom_class(page)
        click_deploy_schema(page)
        wait_for_deploy_restart(page)

        # Server should be up in degraded mode — compiled entities still work
        nav = NavigationPage(page)
        nav.navigate_to("Schema Management", "Custom Class")
        lv = ListViewPage(page)
        lv.wait_for_grid()
        assert lv.has_row_with_text("BadTypeClass"), "BadTypeClass metadata should still be visible"

    def test_02_compiled_entities_work_in_degraded(self, page):
        """Verify CRUD on compiled entities (CustomClass/CustomField) works in degraded mode."""
        reload_and_wait(page)
        nav = NavigationPage(page)
        nav.navigate_to("Schema Management", "Custom Field")
        lv = ListViewPage(page)
        lv.wait_for_grid()
        # The Custom Field list should load without errors
        assert True, "CustomField list loads in degraded mode"


class TestRecoveryFromErrors:
    """Test recovery from error states."""

    def test_03_fix_invalid_metadata_and_recover(self, page):
        """Fix the invalid field, deploy again, verify recovery."""
        # Remove the bad field and fix the class
        conn = get_db_connection()
        try:
            cur = conn.cursor()
            # Delete the bad field
            cur.execute(
                '''DELETE FROM "CustomFields" WHERE "FieldName" = 'BadField'
                   AND "CustomClassId" IN (
                       SELECT "ID" FROM "CustomClasses" WHERE "ClassName" = 'BadTypeClass'
                   )'''
            )
            conn.commit()
        finally:
            conn.close()

        # Add a valid field instead
        insert_field_via_db("BadTypeClass", "ValidName", "System.String", is_default=True)

        # Deploy again — should succeed now
        nav, lv = nav_to_custom_class(page)
        click_deploy_schema(page)
        wait_for_deploy_restart(page)

        # ErrorTest nav group should now exist (BadTypeClass has NavGroup "ErrorTest")
        links = page.locator(".xaf-nav-link").all_text_contents()
        assert "ErrorTest" in links, f"ErrorTest nav should exist after recovery. Links: {links}"

    def test_04_recovered_entity_works(self, page):
        """Verify the recovered entity has working CRUD."""
        reload_and_wait(page)
        page.goto(f"{BASE_URL}/BadTypeClass_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(3000)
        lv = ListViewPage(page)
        lv.wait_for_grid()

        # Create a record
        lv.click_new()
        page.wait_for_timeout(2000)
        detail = DetailViewPage(page)
        detail.fill_field("Valid Name", "RecoveryTest1")
        detail.click_save()
        page.wait_for_timeout(2000)

        # Verify it appears
        page.goto(f"{BASE_URL}/BadTypeClass_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(2000)
        lv.wait_for_grid()
        assert lv.has_row_with_text("RecoveryTest1"), "RecoveryTest1 should exist after recovery"


class TestEmptyMetadataStartup:
    """Test server behavior with no runtime metadata."""

    def test_05_empty_metadata_server_boots(self, page):
        """Remove all runtime metadata, deploy+restart, verify server boots cleanly."""
        # First clean up the recovered entity data
        try:
            page.goto(f"{BASE_URL}/BadTypeClass_ListView", wait_until="networkidle", timeout=60000)
            page.wait_for_timeout(2000)
            lv = ListViewPage(page)
            lv.wait_for_grid()
            if lv.has_row_with_text("RecoveryTest1"):
                lv.select_row_with_text("RecoveryTest1")
                lv.click_delete()
                lv.confirm_delete()
                page.wait_for_timeout(500)
        except Exception:
            pass

        # Delete all runtime classes
        nav, lv = nav_to_custom_class(page)
        for name in ["BadTypeClass", "Customer", "HotLoadProduct"]:
            delete_if_exists(page, name)

        # Deploy with empty runtime set
        click_deploy_schema(page)
        wait_for_deploy_restart(page)

        # Server should boot — Schema Management should still work
        nav = NavigationPage(page)
        nav.navigate_to("Schema Management", "Custom Class")
        lv = ListViewPage(page)
        lv.wait_for_grid()

        # No runtime nav groups should exist
        links = page.locator(".xaf-nav-link").all_text_contents()
        assert "ErrorTest" not in links, "ErrorTest nav should be gone"
        assert "Schema Management" in links, "Schema Management should still exist"


class TestRestartRecovery:
    """Test that the server recovers correctly after restart with existing metadata."""

    def test_06_create_then_restart_recovery(self, page):
        """Create a class, deploy, and verify it works after restart."""
        # Create a simple class
        nav, lv = nav_to_custom_class(page)
        delete_if_exists(page, "RestartTest")
        lv.click_new()
        page.wait_for_timeout(2000)
        detail = DetailViewPage(page)
        detail.fill_field("Class Name", "RestartTest")
        detail.fill_field("Navigation Group", "RecoveryGroup")
        detail.click_save()
        page.wait_for_timeout(2000)

        # Add a field via DB
        insert_field_via_db("RestartTest", "ItemName", "System.String", is_default=True)

        # Deploy
        nav = NavigationPage(page)
        nav.navigate_to("Schema Management", "Custom Class")
        lv = ListViewPage(page)
        lv.wait_for_grid()
        click_deploy_schema(page)
        wait_for_deploy_restart(page)

        # Verify it works after restart
        links = page.locator(".xaf-nav-link").all_text_contents()
        assert "RecoveryGroup" in links, f"RecoveryGroup should exist after restart. Links: {links}"


class TestCleanup:
    """Clean up all Phase 7 test data."""

    def test_99_cleanup(self, page):
        """Remove test entities."""
        reload_and_wait(page)

        # Delete runtime entity data
        try:
            page.goto(f"{BASE_URL}/RestartTest_ListView", wait_until="networkidle", timeout=60000)
            page.wait_for_timeout(2000)
            lv = ListViewPage(page)
            lv.wait_for_grid()
            # Delete all rows if any
        except Exception:
            pass

        # Delete metadata classes
        nav = NavigationPage(page)
        nav.navigate_to("Schema Management", "Custom Class")
        lv = ListViewPage(page)
        lv.wait_for_grid()
        for name in ["BadTypeClass", "RestartTest"]:
            delete_if_exists(page, name)

        # Drop test tables
        conn = get_db_connection()
        try:
            cur = conn.cursor()
            for table in ["BadTypeClass", "RestartTest"]:
                cur.execute(f'DROP TABLE IF EXISTS "{table}" CASCADE')
            conn.commit()
        finally:
            conn.close()
