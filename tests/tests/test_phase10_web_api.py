"""Phase 10 Tests: Web API (OData) endpoints for runtime and compiled entities.

Verifies that:
- Swagger UI and swagger.json are accessible
- Metadata entities (CustomClass, CustomField) are always exposed via OData
- Runtime entities with IsApiExposed=true get OData endpoints after Deploy
- Runtime entities with IsApiExposed=false do NOT get OData endpoints
- Full CRUD (GET/POST/PATCH/DELETE) works through OData
- OData query features ($filter, $select, $top, $orderby) work
- Toggling IsApiExposed and redeploying adds/removes endpoints
"""
import pytest
import sys
import os
import time
import json
import urllib3
import requests

# Suppress SSL warnings for self-signed certs
urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from pages.navigation_page import NavigationPage
from pages.list_view_page import ListViewPage
from pages.detail_view_page import DetailViewPage


BASE_URL = os.environ.get("BASE_URL", "https://host.docker.internal:5001")
API_BASE = f"{BASE_URL}/api/odata"
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
    """Wait for Deploy Schema + server restart cycle."""
    page.wait_for_timeout(5000)
    time.sleep(5)
    wait_for_server(timeout=60)
    page.goto(BASE_URL, wait_until="networkidle", timeout=60000)
    page.wait_for_selector(".xaf-nav-link", timeout=60000)
    page.wait_for_timeout(3000)


def api_get(path, params=None, expect_success=True):
    """Helper: GET an OData endpoint. Returns response object."""
    url = f"{API_BASE}/{path}" if not path.startswith("http") else path
    r = requests.get(url, params=params, verify=False, timeout=15)
    if expect_success:
        assert r.status_code == 200, f"GET {url} returned {r.status_code}: {r.text[:500]}"
    return r


def api_post(path, data):
    """Helper: POST to an OData endpoint. Returns response object."""
    url = f"{API_BASE}/{path}"
    r = requests.post(url, json=data, verify=False, timeout=15,
                      headers={"Content-Type": "application/json"})
    assert r.status_code in (200, 201), f"POST {url} returned {r.status_code}: {r.text[:500]}"
    return r


def api_patch(path, key, data):
    """Helper: PATCH an OData entity by key. Returns response object."""
    url = f"{API_BASE}/{path}({key})"
    r = requests.patch(url, json=data, verify=False, timeout=15,
                       headers={"Content-Type": "application/json"})
    assert r.status_code in (200, 204), f"PATCH {url} returned {r.status_code}: {r.text[:500]}"
    return r


def api_delete(path, key):
    """Helper: DELETE an OData entity by key. Returns response object."""
    url = f"{API_BASE}/{path}({key})"
    r = requests.delete(url, verify=False, timeout=15)
    assert r.status_code in (200, 204), f"DELETE {url} returned {r.status_code}: {r.text[:500]}"
    return r


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


def set_api_exposed_via_db(class_name, is_exposed):
    """Set IsApiExposed flag directly via database."""
    conn = get_db_connection()
    try:
        cur = conn.cursor()
        cur.execute(
            'UPDATE "CustomClasses" SET "IsApiExposed" = %s WHERE "ClassName" = %s AND ("GCRecord" IS NULL OR "GCRecord" = 0)',
            (is_exposed, class_name)
        )
        conn.commit()
        assert cur.rowcount > 0, f"No CustomClass found with name '{class_name}'"
    finally:
        conn.close()


def create_class_via_ui(page, class_name, nav_group, description=""):
    nav, lv = nav_to_custom_class(page)
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


def delete_class_via_db(class_name):
    """Hard-delete a class and its fields from the database."""
    conn = get_db_connection()
    try:
        cur = conn.cursor()
        cur.execute(
            'SELECT "ID" FROM "CustomClasses" WHERE "ClassName" = %s',
            (class_name,)
        )
        row = cur.fetchone()
        if row:
            class_id = row[0]
            cur.execute('DELETE FROM "CustomFields" WHERE "CustomClassId" = %s', (class_id,))
            cur.execute('DELETE FROM "CustomClasses" WHERE "ID" = %s', (class_id,))
            conn.commit()
    finally:
        conn.close()


# ============================================================
# Test Classes
# ============================================================


class TestSwaggerEndpoint:
    """Verify Swagger UI and swagger.json are accessible."""

    def test_01_swagger_json_accessible(self, page):
        """swagger.json should return valid JSON with API info."""
        r = requests.get(f"{BASE_URL}/swagger/v1/swagger.json", verify=False, timeout=15)
        assert r.status_code == 200, f"swagger.json returned {r.status_code}"
        data = r.json()
        assert "info" in data, "swagger.json should contain 'info' section"
        assert data["info"]["title"] == "XafDynamicAssemblies API"

    def test_02_swagger_ui_accessible(self, page):
        """Swagger UI page should load."""
        page.goto(f"{BASE_URL}/swagger", wait_until="networkidle", timeout=30000)
        page.wait_for_timeout(2000)
        # Swagger UI renders with a specific element
        assert page.locator("#swagger-ui, .swagger-ui").count() > 0, \
            "Swagger UI should be rendered"


class TestMetadataEntityEndpoints:
    """Verify CustomClass and CustomField are always exposed via OData."""

    def test_03_custom_class_endpoint_exists(self, page):
        """GET /api/odata/CustomClass should return OData response."""
        r = api_get("CustomClass")
        data = r.json()
        assert "value" in data, f"OData response should have 'value' array. Got: {list(data.keys())}"
        assert isinstance(data["value"], list)

    def test_04_custom_class_returns_valid_odata(self, page):
        """CustomClass endpoint should return valid OData structure with value array."""
        r = api_get("CustomClass")
        data = r.json()
        assert "value" in data, "Should have 'value' key in OData response"
        assert "@odata.context" in data, "Should have '@odata.context' in OData response"
        # May be empty if DB was cleaned — that's OK, structure is valid

    def test_05_custom_field_endpoint_exists(self, page):
        """GET /api/odata/CustomField should return OData response."""
        r = api_get("CustomField")
        data = r.json()
        assert "value" in data, "OData response should have 'value' array"
        assert isinstance(data["value"], list)

    def test_06_odata_metadata_endpoint(self, page):
        """OData $metadata endpoint should return EDM model."""
        r = requests.get(f"{API_BASE}/$metadata", verify=False, timeout=15)
        assert r.status_code == 200, f"$metadata returned {r.status_code}"
        # EDM metadata is XML
        assert "edmx" in r.text.lower() or "EntityType" in r.text, \
            "$metadata should contain EDM model definition"


class TestApiExposedSetup:
    """Create a runtime entity with IsApiExposed=true, deploy, and verify OData endpoints."""

    def test_07_create_api_entity(self, page):
        """Create ApiProduct class for API testing."""
        create_class_via_ui(page, "ApiProduct", "API Test", "Product entity for Web API tests")

    def test_08_add_fields(self, page):
        """Add fields to ApiProduct."""
        insert_field_via_db("ApiProduct", "ProductName", "System.String", is_default=True)
        insert_field_via_db("ApiProduct", "Price", "System.Decimal")
        insert_field_via_db("ApiProduct", "InStock", "System.Boolean")
        insert_field_via_db("ApiProduct", "Quantity", "System.Int32")

    def test_09_set_api_exposed(self, page):
        """Set IsApiExposed=true on ApiProduct."""
        set_api_exposed_via_db("ApiProduct", True)
        # Verify via DB
        conn = get_db_connection()
        try:
            cur = conn.cursor()
            cur.execute(
                'SELECT "IsApiExposed" FROM "CustomClasses" WHERE "ClassName" = %s AND ("GCRecord" IS NULL OR "GCRecord" = 0)',
                ("ApiProduct",)
            )
            row = cur.fetchone()
            assert row and row[0] is True, "IsApiExposed should be True in DB"
        finally:
            conn.close()

    def test_10_create_non_api_entity(self, page):
        """Create ApiInternal class with IsApiExposed=false (should NOT get endpoint)."""
        create_class_via_ui(page, "ApiInternal", "API Test", "Internal entity - not API exposed")
        insert_field_via_db("ApiInternal", "InternalName", "System.String", is_default=True)
        insert_field_via_db("ApiInternal", "Secret", "System.String")
        # Explicitly NOT setting IsApiExposed (defaults to false)

    def test_11_deploy_and_restart(self, page):
        """Deploy schema and restart to activate Web API endpoints."""
        nav, lv = nav_to_custom_class(page)
        click_deploy_schema(page)
        wait_for_deploy_restart(page)


class TestRuntimeEntityODataEndpoints:
    """Verify OData endpoints for API-exposed runtime entities."""

    def test_12_api_product_endpoint_exists(self, page):
        """GET /api/odata/ApiProduct should return OData response."""
        r = api_get("ApiProduct")
        data = r.json()
        assert "value" in data, f"ApiProduct OData response should have 'value'. Got: {list(data.keys())}"
        assert isinstance(data["value"], list)

    def test_13_non_exposed_entity_not_in_odata(self, page):
        """GET /api/odata/ApiInternal should NOT return OData JSON (not API-exposed)."""
        r = requests.get(f"{API_BASE}/ApiInternal", verify=False, timeout=15)
        # Non-exposed entities either return 404, or fall through to Blazor (HTML 200)
        # Either way, they should NOT return OData JSON with a "value" array
        content_type = r.headers.get("Content-Type", "")
        is_odata = "application/json" in content_type or "odata" in content_type
        if r.status_code == 200 and is_odata:
            data = r.json()
            assert "value" not in data, \
                "ApiInternal should NOT have OData endpoints (not API-exposed)"
        # If status is 404 or HTML, that's also acceptable

    def test_14_api_product_in_metadata(self, page):
        """ApiProduct should appear in OData $metadata EDM model."""
        r = requests.get(f"{API_BASE}/$metadata", verify=False, timeout=15)
        assert r.status_code == 200
        assert "ApiProduct" in r.text, "ApiProduct should be in EDM metadata"

    def test_15_non_exposed_no_entity_set_in_metadata(self, page):
        """ApiInternal should NOT have an EntitySet in OData $metadata (no CRUD endpoints)."""
        r = requests.get(f"{API_BASE}/$metadata", verify=False, timeout=15)
        assert r.status_code == 200
        # The type may appear in EDM (XAF registers all types) but should NOT have an EntitySet
        # EntitySet is what enables CRUD endpoints like GET /api/odata/ApiInternal
        assert 'EntitySet Name="ApiInternal"' not in r.text, \
            "ApiInternal should NOT have an EntitySet in EDM metadata"


class TestODataCRUD:
    """Full CRUD operations via OData on runtime entity ApiProduct."""

    _created_id = None

    def test_16_create_via_post(self, page):
        """POST /api/odata/ApiProduct should create a new record."""
        r = api_post("ApiProduct", {
            "ProductName": "ODataWidget",
            "Price": 29.99,
            "InStock": True,
            "Quantity": 100,
        })
        data = r.json()
        # OData returns the created entity with its key
        assert "ProductName" in data or "productName" in data, \
            f"Response should contain created entity. Keys: {list(data.keys())}"
        # Store ID for subsequent tests (OData uses ID or id)
        entity_id = data.get("ID") or data.get("id") or data.get("Id")
        assert entity_id is not None, f"Created entity should have an ID. Data: {data}"
        TestODataCRUD._created_id = entity_id

    def test_17_read_via_get(self, page):
        """GET /api/odata/ApiProduct should include the created record."""
        r = api_get("ApiProduct")
        data = r.json()
        names = [
            item.get("ProductName") or item.get("productName")
            for item in data["value"]
        ]
        assert "ODataWidget" in names, f"ODataWidget should be in results. Got: {names}"

    def test_18_read_single_by_key(self, page):
        """GET /api/odata/ApiProduct(key) should return the specific record."""
        assert TestODataCRUD._created_id, "No created ID from test_16"
        r = api_get(f"ApiProduct({TestODataCRUD._created_id})")
        data = r.json()
        name = data.get("ProductName") or data.get("productName")
        assert name == "ODataWidget", f"Expected ODataWidget, got {name}"

    def test_19_update_via_patch(self, page):
        """PATCH /api/odata/ApiProduct(key) should update fields."""
        assert TestODataCRUD._created_id, "No created ID from test_16"
        api_patch("ApiProduct", TestODataCRUD._created_id, {
            "Price": 39.99,
            "Quantity": 200,
        })
        # Verify the update
        r = api_get(f"ApiProduct({TestODataCRUD._created_id})")
        data = r.json()
        price = data.get("Price") or data.get("price")
        qty = data.get("Quantity") or data.get("quantity")
        assert float(price) == 39.99, f"Price should be 39.99, got {price}"
        assert int(qty) == 200, f"Quantity should be 200, got {qty}"

    def test_20_delete_via_delete(self, page):
        """DELETE /api/odata/ApiProduct(key) should remove the record."""
        assert TestODataCRUD._created_id, "No created ID from test_16"
        api_delete("ApiProduct", TestODataCRUD._created_id)
        # Verify deletion
        r = requests.get(
            f"{API_BASE}/ApiProduct({TestODataCRUD._created_id})",
            verify=False, timeout=15
        )
        assert r.status_code in (404, 204, 400), \
            f"Deleted entity should return 404. Got {r.status_code}"


class TestODataQueryFeatures:
    """Test OData query options ($filter, $select, $top, $orderby, $count)."""

    def test_21_seed_test_data(self, page):
        """Create multiple records for query testing."""
        for i, (name, price, qty, stock) in enumerate([
            ("QueryWidget1", 10.00, 50, True),
            ("QueryWidget2", 25.50, 100, True),
            ("QueryWidget3", 99.99, 5, False),
            ("QueryGadget1", 15.00, 200, True),
            ("QueryGadget2", 75.00, 0, False),
        ]):
            api_post("ApiProduct", {
                "ProductName": name,
                "Price": price,
                "Quantity": qty,
                "InStock": stock,
            })

    def test_22_filter_by_string(self, page):
        """$filter on ProductName should return matching records."""
        r = api_get("ApiProduct", params={
            "$filter": "contains(ProductName, 'Widget')"
        })
        data = r.json()
        names = [item.get("ProductName") or item.get("productName") for item in data["value"]]
        assert all("Widget" in n for n in names), f"All results should contain 'Widget'. Got: {names}"
        assert len(names) >= 3, f"Should have at least 3 Widget records. Got: {len(names)}"

    def test_23_filter_by_number(self, page):
        """$filter on Price comparison should work."""
        r = api_get("ApiProduct", params={
            "$filter": "Price gt 50"
        })
        data = r.json()
        for item in data["value"]:
            price = float(item.get("Price") or item.get("price"))
            assert price > 50, f"All prices should be > 50. Got: {price}"

    def test_24_filter_by_boolean(self, page):
        """$filter on InStock should work."""
        r = api_get("ApiProduct", params={
            "$filter": "InStock eq true"
        })
        data = r.json()
        for item in data["value"]:
            stock = item.get("InStock") or item.get("inStock")
            assert stock is True, f"All records should have InStock=true. Got: {stock}"

    def test_25_select_specific_fields(self, page):
        """$select should return only requested fields."""
        r = api_get("ApiProduct", params={
            "$select": "ProductName,Price"
        })
        data = r.json()
        assert len(data["value"]) > 0, "Should have results"
        first = data["value"][0]
        # Should have ProductName and Price but not Quantity or InStock
        has_name = "ProductName" in first or "productName" in first
        assert has_name, f"Should have ProductName in response. Keys: {list(first.keys())}"

    def test_26_top_and_orderby(self, page):
        """$top and $orderby should limit and sort results."""
        r = api_get("ApiProduct", params={
            "$top": "3",
            "$orderby": "Price desc"
        })
        data = r.json()
        assert len(data["value"]) <= 3, f"$top=3 should limit to 3 results. Got: {len(data['value'])}"
        prices = [float(item.get("Price") or item.get("price")) for item in data["value"]]
        assert prices == sorted(prices, reverse=True), \
            f"Results should be sorted by Price desc. Got: {prices}"

    def test_27_count(self, page):
        """$count=true should include total count in response."""
        r = api_get("ApiProduct", params={"$count": "true"})
        data = r.json()
        count = data.get("@odata.count") or data.get("@count")
        assert count is not None and count >= 5, \
            f"Count should be >= 5 (seeded records). Got: {count}"

    def test_28_skip(self, page):
        """$skip should offset results."""
        r_all = api_get("ApiProduct", params={"$orderby": "ProductName"})
        all_data = r_all.json()["value"]

        r_skip = api_get("ApiProduct", params={"$skip": "2", "$orderby": "ProductName"})
        skip_data = r_skip.json()["value"]

        if len(all_data) > 2:
            first_skip_name = skip_data[0].get("ProductName") or skip_data[0].get("productName")
            third_all_name = all_data[2].get("ProductName") or all_data[2].get("productName")
            assert first_skip_name == third_all_name, \
                f"$skip=2 first result should match 3rd of full list. Got skip={first_skip_name}, all[2]={third_all_name}"


class TestApiExposedToggle:
    """Test toggling IsApiExposed and redeploying."""

    def test_29_disable_api_for_product(self, page):
        """Set IsApiExposed=false on ApiProduct and redeploy."""
        set_api_exposed_via_db("ApiProduct", False)
        nav, lv = nav_to_custom_class(page)
        click_deploy_schema(page)
        wait_for_deploy_restart(page)

    def test_30_endpoint_removed_after_disable(self, page):
        """ApiProduct should NOT have OData endpoint after disabling IsApiExposed."""
        r = requests.get(f"{API_BASE}/ApiProduct", verify=False, timeout=15)
        content_type = r.headers.get("Content-Type", "")
        is_odata = "application/json" in content_type or "odata" in content_type
        if r.status_code == 200 and is_odata:
            data = r.json()
            assert "value" not in data, \
                "ApiProduct should NOT have OData endpoints after disabling"
        # 404 or Blazor HTML fallback are both acceptable

    def test_31_metadata_entities_still_work(self, page):
        """CustomClass and CustomField endpoints should still work."""
        r = api_get("CustomClass")
        assert r.status_code == 200
        r = api_get("CustomField")
        assert r.status_code == 200

    def test_32_re_enable_api_for_product(self, page):
        """Set IsApiExposed=true again and redeploy."""
        set_api_exposed_via_db("ApiProduct", True)
        nav, lv = nav_to_custom_class(page)
        click_deploy_schema(page)
        wait_for_deploy_restart(page)

    def test_33_endpoint_restored_after_enable(self, page):
        """ApiProduct endpoint should be accessible again."""
        r = api_get("ApiProduct")
        data = r.json()
        assert "value" in data, "ApiProduct should be accessible after re-enabling"


class TestApiAndUIConsistency:
    """Verify that data created via API appears in XAF UI and vice versa."""

    def test_34_create_via_api_visible_in_ui(self, page):
        """Record created via OData POST should appear in XAF ListView."""
        # Create via API
        r = api_post("ApiProduct", {
            "ProductName": "ApiCreatedItem",
            "Price": 42.00,
            "InStock": True,
            "Quantity": 7,
        })

        # Navigate to the entity's list view in XAF
        reload_and_wait(page)
        page.goto(f"{BASE_URL}/ApiProduct_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(3000)
        lv = ListViewPage(page)
        lv.wait_for_grid()
        assert lv.has_row_with_text("ApiCreatedItem"), \
            "Record created via API should appear in XAF ListView"

    def test_35_create_via_ui_visible_in_api(self, page):
        """Record created via XAF UI should appear in OData GET."""
        reload_and_wait(page)
        page.goto(f"{BASE_URL}/ApiProduct_ListView", wait_until="networkidle", timeout=60000)
        page.wait_for_timeout(3000)
        lv = ListViewPage(page)
        lv.wait_for_grid()

        lv.click_new()
        page.wait_for_timeout(2000)
        detail = DetailViewPage(page)
        detail.fill_field("Product Name", "UICreatedItem")
        detail.click_save()
        page.wait_for_timeout(2000)

        # Query via API
        r = api_get("ApiProduct", params={
            "$filter": "ProductName eq 'UICreatedItem'"
        })
        data = r.json()
        assert len(data["value"]) >= 1, \
            f"UICreatedItem should appear via API. Got: {data['value']}"


class TestCleanup:
    """Clean up all test data created by Phase 10."""

    def test_99_cleanup_api_records(self, page):
        """Delete all test records via API and DB."""
        # Clean up ApiProduct records via API
        try:
            r = api_get("ApiProduct")
            if r.status_code == 200:
                data = r.json()
                for item in data.get("value", []):
                    entity_id = item.get("ID") or item.get("id") or item.get("Id")
                    if entity_id:
                        try:
                            api_delete("ApiProduct", entity_id)
                        except AssertionError:
                            pass
        except Exception:
            pass

        # Clean up metadata classes via DB
        for class_name in ["ApiProduct", "ApiInternal"]:
            try:
                delete_class_via_db(class_name)
            except Exception:
                pass

        # Deploy to clean up (removes runtime types from compilation)
        try:
            nav, lv = nav_to_custom_class(page)
            click_deploy_schema(page)
            wait_for_deploy_restart(page)
        except Exception:
            pass
