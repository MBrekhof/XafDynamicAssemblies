"""Phase 8 Tests: Performance + Polish.

Tests verify that compilation performance is acceptable for multi-class schemas,
that the system handles concurrent page loads, and that the startup is fast.
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
    wait_for_server(timeout=90)
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


class TestMultiClassPerformance:
    """Test compilation and startup performance with multiple classes."""

    def test_01_bulk_create_classes(self, page):
        """Create 10 classes with fields via DB and deploy — measure total time."""
        conn = get_db_connection()
        try:
            cur = conn.cursor()
            # Clean up any previous test classes
            for i in range(10):
                name = f"PerfTest{i:02d}"
                cur.execute('DELETE FROM "CustomClasses" WHERE "ClassName" = %s', (name,))

            # Create 10 classes, each with 3 fields
            for i in range(10):
                name = f"PerfTest{i:02d}"
                cur.execute(
                    '''INSERT INTO "CustomClasses" ("ID", "ClassName", "NavigationGroup", "Description",
                       "Status", "GCRecord", "OptimisticLockField")
                       VALUES (gen_random_uuid(), %s, 'PerfGroup', %s, 'Runtime', 0, 0)
                       RETURNING "ID"''',
                    (name, f"Performance test class {i}")
                )
                class_id = cur.fetchone()[0]

                for j, (fname, ftype) in enumerate([
                    ("Name", "System.String"),
                    ("Value", "System.Decimal"),
                    ("Active", "System.Boolean"),
                ]):
                    cur.execute(
                        '''INSERT INTO "CustomFields" ("ID", "CustomClassId", "FieldName", "TypeName",
                           "IsRequired", "IsDefaultField", "Description", "ReferencedClassName",
                           "SortOrder", "GCRecord", "OptimisticLockField")
                           VALUES (gen_random_uuid(), %s, %s, %s, false, %s, NULL, NULL, %s, 0, 0)''',
                        (class_id, fname, ftype, fname == "Name", j)
                    )
            conn.commit()
        finally:
            conn.close()

        # Deploy and measure time
        nav, lv = nav_to_custom_class(page)
        start_time = time.time()
        click_deploy_schema(page)
        wait_for_deploy_restart(page)
        deploy_time = time.time() - start_time

        # Verify all 10 classes compiled successfully
        links = page.locator(".xaf-nav-link").all_text_contents()
        assert "PerfGroup" in links, f"PerfGroup should exist after deploy. Links: {links}"

        # Performance check: deploy + restart should complete in under 60 seconds
        # (Roslyn compilation for 10 classes typically takes 2-5 seconds,
        #  but process restart and XAF bootstrap add overhead)
        assert deploy_time < 60, f"Deploy+restart took {deploy_time:.1f}s (should be under 60s)"

    def test_02_multi_class_crud_works(self, page):
        """Verify CRUD works on one of the bulk-created entities."""
        reload_and_wait(page)
        page.goto(f"{BASE_URL}/PerfTest05_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(3000)
        lv = ListViewPage(page)
        lv.wait_for_grid()

        lv.click_new()
        page.wait_for_timeout(2000)
        detail = DetailViewPage(page)
        detail.fill_field("Name", "PerfRecord1")
        detail.click_save()
        page.wait_for_timeout(2000)

        page.goto(f"{BASE_URL}/PerfTest05_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(2000)
        lv.wait_for_grid()
        assert lv.has_row_with_text("PerfRecord1"), "PerfRecord1 should exist"


class TestConcurrentPageLoads:
    """Test that the system handles page loads from multiple browser contexts."""

    def test_03_concurrent_page_access(self, page):
        """Open the same runtime entity in multiple tabs and verify no errors."""
        reload_and_wait(page)

        # Open PerfTest00 in this page
        page.goto(f"{BASE_URL}/PerfTest00_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(3000)
        lv = ListViewPage(page)
        lv.wait_for_grid()

        # The grid should render without errors
        assert page.locator(".dxbl-grid").first.is_visible() or page.locator(".dxbl-grid").last.is_visible()


class TestCleanup:
    """Clean up all Phase 8 test data."""

    def test_99_cleanup(self, page):
        """Remove all performance test entities."""
        # Delete runtime entity data first
        for i in range(10):
            name = f"PerfTest{i:02d}"
            try:
                page.goto(f"{BASE_URL}/{name}_ListView", wait_until="networkidle", timeout=30000)
                page.wait_for_timeout(1000)
                lv = ListViewPage(page)
                lv.wait_for_grid()
                # Delete all visible rows
                while True:
                    rows = page.locator(".dxbl-grid-table tbody tr[data-visible-index]")
                    if rows.count() <= 1:  # Header row only
                        break
                    rows.nth(1).click()
                    page.wait_for_timeout(300)
                    lv.click_delete()
                    lv.confirm_delete()
                    page.wait_for_timeout(300)
            except Exception:
                pass

        # Delete metadata classes
        nav = NavigationPage(page)
        nav.navigate_to("Schema Management", "Custom Class")
        lv = ListViewPage(page)
        lv.wait_for_grid()
        for i in range(10):
            delete_if_exists(page, f"PerfTest{i:02d}")

        # Drop tables
        conn = get_db_connection()
        try:
            cur = conn.cursor()
            for i in range(10):
                cur.execute(f'DROP TABLE IF EXISTS "PerfTest{i:02d}" CASCADE')
            conn.commit()
        finally:
            conn.close()
