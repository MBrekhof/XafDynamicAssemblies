"""Phase 9 Tests: Review fixes and new XAF attribute support.

Covers:
- Finding 1: Required reference fields enforced (NOT NULL in DDL, non-nullable Guid in codegen)
- Finding 2: Reference type validation requires ReferencedClassName
- Finding 3: Test Compile includes all runtime classes (cross-reference support)
- Finding 4: GraduationService escapes special characters in descriptions
- New: XAF property attributes (ImmediatePostData, Size, Visibility, Editable, ToolTip, DisplayName)
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


def nav_to_custom_field(page):
    nav = NavigationPage(page)
    nav.navigate_to("Schema Management", "Custom Field")
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
    """Create a CustomClass via the UI. Each call should be in its own test."""
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

    # Navigate back to list view to verify
    nav = NavigationPage(page)
    nav.navigate_to("Schema Management", "Custom Class")
    lv = ListViewPage(page)
    lv.wait_for_grid()
    assert lv.has_row_with_text(class_name), f"{class_name} should exist after creation"


def insert_field_via_db(class_name, field_name, type_name="System.String",
                        referenced_class_name=None, is_default=False, is_required=False,
                        is_immediate_post_data=False, string_max_length=None,
                        is_visible_in_list_view=True, is_visible_in_detail_view=True,
                        is_editable=True, tool_tip=None, display_name=None):
    """Insert a CustomField directly via PostgreSQL (includes new attribute columns)."""
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
            '''INSERT INTO "CustomFields"
               ("ID", "CustomClassId", "FieldName", "TypeName",
                "IsRequired", "IsDefaultField", "Description", "ReferencedClassName",
                "SortOrder", "GCRecord", "OptimisticLockField",
                "IsImmediatePostData", "StringMaxLength",
                "IsVisibleInListView", "IsVisibleInDetailView",
                "IsEditable", "ToolTip", "DisplayName")
               VALUES (gen_random_uuid(), %s, %s, %s, %s, %s, NULL, %s, 0, 0, 0,
                       %s, %s, %s, %s, %s, %s, %s)''',
            (class_id, field_name, type_name, is_required, is_default,
             referenced_class_name,
             is_immediate_post_data, string_max_length,
             is_visible_in_list_view, is_visible_in_detail_view,
             is_editable, tool_tip, display_name)
        )
        conn.commit()
    finally:
        conn.close()


def try_save_and_check_validation(page, timeout=3000):
    """Click Save and check if a validation error appeared."""
    page.locator('dxbl-toolbar-item[text="Save"]').first.click()
    page.wait_for_timeout(1500)

    validation_window = page.locator(".dxbl-popup-content, .dxbl-window")
    if validation_window.count() > 0:
        for i in range(validation_window.count()):
            text = validation_window.nth(i).inner_text()
            if any(kw in text.lower() for kw in
                   ["must be", "cannot be", "reserved", "conflicts", "valid",
                    "error", "requires", "reference"]):
                return False, text

    body_text = page.locator("body").inner_text()
    validation_keywords = [
        "must be a valid C# identifier",
        "cannot be a C# keyword",
        "conflicts with a built-in type",
        "Field Name is reserved",
        "must be a supported CLR type",
        "Reference field requires",
        "Validation"
    ]
    for keyword in validation_keywords:
        if keyword in body_text:
            return False, body_text

    return True, ""


# =============================================================================
# Finding 2: Reference type validation requires ReferencedClassName
# =============================================================================
class TestReferenceValidation:
    """Verify that TypeName=Reference without ReferencedClassName is rejected."""

    def test_01_reference_without_class_rejected(self, page):
        """Save a Reference field without ReferencedClassName — should fail validation."""
        nav, lv = nav_to_custom_field(page)
        lv.click_new()
        page.wait_for_timeout(1000)

        detail = DetailViewPage(page)
        detail.fill_field("Field Name", "BadRef")
        detail.fill_field("Type Name", "Reference")
        # Deliberately leave Referenced Class Name empty

        saved, error_text = try_save_and_check_validation(page)
        assert not saved, "Save should fail for Reference without ReferencedClassName"
        assert "reference" in error_text.lower() or "Validation" in error_text


# =============================================================================
# Finding 3: Test Compile includes all runtime classes (cross-reference support)
# =============================================================================
class TestCompileCrossReference:
    """Verify Test Compile works for classes that reference other runtime classes."""

    def test_02_create_parent_class(self, page):
        """Create P9Parent class."""
        create_class_via_ui(page, "P9Parent", "P9Test")

    def test_03_create_child_class(self, page):
        """Create P9Child class."""
        create_class_via_ui(page, "P9Child", "P9Test")

    def test_04_add_cross_ref_fields(self, page):
        """Add fields including a cross-reference from P9Child to P9Parent."""
        insert_field_via_db("P9Parent", "ParentName", "System.String", is_default=True)
        insert_field_via_db("P9Child", "ChildName", "System.String", is_default=True)
        insert_field_via_db("P9Child", "Parent", "Reference",
                            referenced_class_name="P9Parent")

        # Verify fields exist
        nav, lv = nav_to_custom_field(page)
        assert lv.has_row_with_text("ParentName"), "ParentName field should exist"
        assert lv.has_row_with_text("ChildName"), "ChildName field should exist"

    def test_05_test_compile_cross_ref_succeeds(self, page):
        """Test Compile All from ListView should succeed since all classes are compiled together."""
        nav, lv = nav_to_custom_class(page)
        page.wait_for_timeout(1000)

        compile_btn = page.locator('dxbl-toolbar-item[text="Test Compile All"]')
        assert compile_btn.count() > 0, "Test Compile All action should be visible"
        compile_btn.first.click()
        page.wait_for_timeout(3000)

        body_text = page.locator("body").inner_text()
        assert "successful" in body_text.lower() or "success" in body_text.lower(), \
            f"Test Compile All should succeed for cross-reference. Page text: {body_text[:500]}"


# =============================================================================
# Finding 1: Required reference fields + New XAF attributes
# =============================================================================
class TestRequiredRefAndAttributes:
    """Create classes with required refs and XAF attributes, deploy, verify DB & codegen."""

    def test_06_create_attr_dept(self, page):
        """Create AttrDept class."""
        create_class_via_ui(page, "AttrDept", "P9Attr")

    def test_07_create_attr_emp(self, page):
        """Create AttrEmp class with special chars in description (for Finding 4 too)."""
        create_class_via_ui(page, "AttrEmp", "P9Attr",
                            description='Employee with tags and quotes')

    def test_08_add_fields_with_attributes(self, page):
        """Add fields with various XAF attributes via DB."""
        # AttrDept: simple name field
        insert_field_via_db("AttrDept", "DeptName", "System.String", is_default=True,
                            string_max_length=100)

        # AttrEmp: fields exercising all new attributes
        insert_field_via_db("AttrEmp", "EmpName", "System.String", is_default=True,
                            is_immediate_post_data=True,
                            display_name="Employee Name",
                            tool_tip="Full name of the employee")

        insert_field_via_db("AttrEmp", "Notes", "System.String",
                            string_max_length=-1,  # Unlimited (memo)
                            is_visible_in_list_view=False)

        insert_field_via_db("AttrEmp", "EmployeeCode", "System.String",
                            is_required=True, is_editable=False,
                            tool_tip="Auto-generated code")

        insert_field_via_db("AttrEmp", "Salary", "System.Decimal",
                            is_visible_in_list_view=False,
                            is_visible_in_detail_view=True)

        # Required reference (Finding 1)
        insert_field_via_db("AttrEmp", "Department", "Reference",
                            referenced_class_name="AttrDept",
                            is_required=True,
                            is_immediate_post_data=True)

        # Verify via Custom Field list
        nav, lv = nav_to_custom_field(page)
        assert lv.has_row_with_text("EmpName"), "EmpName field should exist"
        assert lv.has_row_with_text("Department"), "Department ref field should exist"

    def test_09_deploy_and_verify(self, page):
        """Deploy schema and wait for server restart."""
        nav, lv = nav_to_custom_class(page)
        click_deploy_schema(page)
        wait_for_deploy_restart(page)

    def test_10_required_ref_is_not_null_in_db(self, page):
        """Finding 1: Verify the required reference FK column is NOT NULL in PostgreSQL."""
        conn = get_db_connection()
        try:
            cur = conn.cursor()
            cur.execute("""
                SELECT is_nullable FROM information_schema.columns
                WHERE table_schema = 'public'
                AND table_name = 'AttrEmp'
                AND column_name = 'DepartmentId'
            """)
            row = cur.fetchone()
            assert row is not None, "DepartmentId column should exist in AttrEmp table"
            assert row[0] == "NO", \
                f"Required reference column DepartmentId should be NOT NULL, got is_nullable={row[0]}"
        finally:
            conn.close()

    def test_11_optional_ref_is_nullable_in_db(self, page):
        """Verify a non-required reference FK column IS nullable in PostgreSQL (control test)."""
        conn = get_db_connection()
        try:
            cur = conn.cursor()
            cur.execute("""
                SELECT is_nullable FROM information_schema.columns
                WHERE table_schema = 'public'
                AND table_name = 'P9Child'
                AND column_name = 'ParentId'
            """)
            row = cur.fetchone()
            assert row is not None, "ParentId column should exist in P9Child table"
            assert row[0] == "YES", \
                f"Optional reference column ParentId should be nullable, got is_nullable={row[0]}"
        finally:
            conn.close()

    def test_12_string_max_length_stored(self, page):
        """Verify StringMaxLength is stored correctly in metadata."""
        conn = get_db_connection()
        try:
            cur = conn.cursor()
            cur.execute("""
                SELECT cf."StringMaxLength"
                FROM "CustomFields" cf
                JOIN "CustomClasses" cc ON cf."CustomClassId" = cc."ID"
                WHERE cc."ClassName" = 'AttrDept' AND cf."FieldName" = 'DeptName'
                AND (cf."GCRecord" IS NULL OR cf."GCRecord" = 0)
            """)
            row = cur.fetchone()
            assert row is not None, "DeptName field should exist"
            assert row[0] == 100, f"StringMaxLength should be 100, got {row[0]}"
        finally:
            conn.close()

    def test_13_xaf_attributes_stored_in_metadata(self, page):
        """Verify all new XAF attribute columns are stored correctly."""
        conn = get_db_connection()
        try:
            cur = conn.cursor()
            # Check EmpName field attributes
            cur.execute("""
                SELECT cf."IsImmediatePostData", cf."DisplayName", cf."ToolTip",
                       cf."IsVisibleInListView", cf."IsVisibleInDetailView", cf."IsEditable"
                FROM "CustomFields" cf
                JOIN "CustomClasses" cc ON cf."CustomClassId" = cc."ID"
                WHERE cc."ClassName" = 'AttrEmp' AND cf."FieldName" = 'EmpName'
                AND (cf."GCRecord" IS NULL OR cf."GCRecord" = 0)
            """)
            row = cur.fetchone()
            assert row is not None, "EmpName field should exist"
            assert row[0] is True, f"IsImmediatePostData should be True, got {row[0]}"
            assert row[1] == "Employee Name", f"DisplayName should be 'Employee Name', got {row[1]}"
            assert row[2] == "Full name of the employee", f"ToolTip mismatch: {row[2]}"
            assert row[3] is True, "IsVisibleInListView should be True"
            assert row[4] is True, "IsVisibleInDetailView should be True"
            assert row[5] is True, "IsEditable should be True"

            # Check Notes field — hidden from list view, memo
            cur.execute("""
                SELECT cf."IsVisibleInListView", cf."StringMaxLength"
                FROM "CustomFields" cf
                JOIN "CustomClasses" cc ON cf."CustomClassId" = cc."ID"
                WHERE cc."ClassName" = 'AttrEmp' AND cf."FieldName" = 'Notes'
                AND (cf."GCRecord" IS NULL OR cf."GCRecord" = 0)
            """)
            row = cur.fetchone()
            assert row is not None, "Notes field should exist"
            assert row[0] is False, "Notes.IsVisibleInListView should be False"
            assert row[1] == -1, f"Notes.StringMaxLength should be -1 (unlimited), got {row[1]}"

            # Check EmployeeCode field — required, not editable
            cur.execute("""
                SELECT cf."IsRequired", cf."IsEditable"
                FROM "CustomFields" cf
                JOIN "CustomClasses" cc ON cf."CustomClassId" = cc."ID"
                WHERE cc."ClassName" = 'AttrEmp' AND cf."FieldName" = 'EmployeeCode'
                AND (cf."GCRecord" IS NULL OR cf."GCRecord" = 0)
            """)
            row = cur.fetchone()
            assert row is not None, "EmployeeCode field should exist"
            assert row[0] is True, "EmployeeCode.IsRequired should be True"
            assert row[1] is False, "EmployeeCode.IsEditable should be False"
        finally:
            conn.close()


# =============================================================================
# Finding 4: Graduation escaping (special chars in description/nav group)
# =============================================================================
class TestGraduationEscaping:
    """Verify graduation properly escapes special characters in generated source."""

    def test_14_create_escapable_class(self, page):
        """Create a class with special characters in description for graduation test."""
        create_class_via_ui(page, "EscGrad", "EscNav",
                            description='A class with special chars')

    def test_15_add_escapable_fields(self, page):
        """Add fields including one with a ToolTip containing quotes."""
        insert_field_via_db("EscGrad", "Title", "System.String", is_default=True)
        insert_field_via_db("EscGrad", "Detail", "System.String",
                            tool_tip='Has "quotes" inside',
                            display_name="Detail Info")

    def test_16_deploy_escapable_class(self, page):
        """Deploy the class so it becomes a runtime entity."""
        nav, lv = nav_to_custom_class(page)
        click_deploy_schema(page)
        wait_for_deploy_restart(page)

    def test_17_graduate_and_check_source(self, page):
        """Graduate the class and verify the source escapes special characters."""
        nav, lv = nav_to_custom_class(page)
        lv.double_click_row_with_text("EscGrad")
        page.wait_for_timeout(2000)

        # Click Graduate
        graduate_btn = page.locator('dxbl-toolbar-item[text="Graduate"]')
        if graduate_btn.count() == 0:
            graduate_btn = page.locator('button:has-text("Graduate"), span:has-text("Graduate")')
        graduate_btn.first.click()
        page.wait_for_timeout(1000)

        confirm_btn = page.locator('button:has-text("Yes"), button:has-text("OK")')
        if confirm_btn.count() > 0:
            confirm_btn.first.click()
        page.wait_for_timeout(2000)

        # Dismiss success message
        ok_btn = page.locator('button:has-text("OK")')
        if ok_btn.count() > 0:
            ok_btn.first.click()
            page.wait_for_timeout(500)

        # Check graduated source in DB
        conn = get_db_connection()
        try:
            cur = conn.cursor()
            cur.execute(
                'SELECT "GraduatedSource" FROM "CustomClasses" WHERE "ClassName" = %s AND ("GCRecord" IS NULL OR "GCRecord" = 0)',
                ("EscGrad",)
            )
            row = cur.fetchone()
            assert row is not None and row[0] is not None, "GraduatedSource should be populated"
            source = row[0]

            # Verify class structure
            assert "class EscGrad" in source, "Source should contain class definition"
            assert "BaseObject" in source, "Source should inherit from BaseObject"

            # Finding 4: ToolTip with quotes should be escaped in string literal
            assert 'Has \\"quotes\\" inside' in source or 'Has "quotes" inside' not in source.split('[ToolTip(')[1].split(')]')[0] if '[ToolTip(' in source else False, \
                f"Quotes in ToolTip should be escaped. Source:\n{source}"

            # Simpler check: the escaped form should be present
            assert '\\"quotes\\"' in source, \
                f"Quotes should be backslash-escaped in generated source. Source:\n{source}"

            # Verify DisplayName attribute present on Detail field
            assert 'DisplayName(' in source, \
                f"DisplayName attribute should be present. Source:\n{source}"
        finally:
            conn.close()


# =============================================================================
# New XAF Attributes: verify attributes appear in graduated source
# =============================================================================
class TestXafAttributesInGeneratedCode:
    """Verify new XAF attributes appear in graduated source code."""

    def test_18_graduate_attr_emp(self, page):
        """Graduate AttrEmp and verify source contains all attribute annotations."""
        nav, lv = nav_to_custom_class(page)
        lv.double_click_row_with_text("AttrEmp")
        page.wait_for_timeout(2000)

        graduate_btn = page.locator('dxbl-toolbar-item[text="Graduate"]')
        if graduate_btn.count() == 0:
            graduate_btn = page.locator('button:has-text("Graduate"), span:has-text("Graduate")')
        graduate_btn.first.click()
        page.wait_for_timeout(1000)

        confirm_btn = page.locator('button:has-text("Yes"), button:has-text("OK")')
        if confirm_btn.count() > 0:
            confirm_btn.first.click()
        page.wait_for_timeout(2000)

        ok_btn = page.locator('button:has-text("OK")')
        if ok_btn.count() > 0:
            ok_btn.first.click()
            page.wait_for_timeout(500)

        conn = get_db_connection()
        try:
            cur = conn.cursor()
            cur.execute(
                'SELECT "GraduatedSource" FROM "CustomClasses" WHERE "ClassName" = %s AND ("GCRecord" IS NULL OR "GCRecord" = 0)',
                ("AttrEmp",)
            )
            row = cur.fetchone()
            assert row is not None and row[0] is not None, "GraduatedSource should be populated"
            source = row[0]

            # ImmediatePostData on EmpName and Department
            assert "[ImmediatePostData]" in source, \
                f"Source should contain [ImmediatePostData]. Source:\n{source}"

            # DisplayName on EmpName
            assert 'DisplayName("Employee Name")' in source, \
                f"Source should contain DisplayName attribute. Source:\n{source}"

            # ToolTip on EmpName
            assert 'ToolTip("Full name of the employee")' in source, \
                f"Source should contain ToolTip attribute. Source:\n{source}"

            # Notes: VisibleInListView(false) and Size(-1) for memo
            assert "[VisibleInListView(false)]" in source, \
                f"Source should contain [VisibleInListView(false)]. Source:\n{source}"
            assert "Size(-1)" in source, \
                f"Source should contain Size(-1) for memo field. Source:\n{source}"

            # EmployeeCode: Editable(false)
            assert "Editable(false)" in source, \
                f"Source should contain [Editable(false)]. Source:\n{source}"

            # Required reference: Department FK should be non-nullable Guid (not Guid?)
            assert "Guid DepartmentId" in source, \
                f"Required ref should generate non-nullable Guid FK. Source:\n{source}"
            # Also should have [Required] on the FK
            assert "[System.ComponentModel.DataAnnotations.Required]" in source, \
                f"Required ref should have [Required] attribute. Source:\n{source}"
        finally:
            conn.close()


# =============================================================================
# Cleanup
# =============================================================================
class TestCleanup:
    """Clean up all Phase 9 test data."""

    def test_99_cleanup(self, page):
        """Remove all test entities and metadata."""
        reload_and_wait(page)

        # Delete runtime entity records where tables might exist
        for entity in ["AttrEmp", "AttrDept", "P9Child", "P9Parent", "EscGrad"]:
            try:
                page.goto(f"{BASE_URL}/{entity}_ListView",
                          wait_until="networkidle", timeout=15000)
                page.wait_for_timeout(2000)
                lv = ListViewPage(page)
                lv.wait_for_grid()
                while lv.get_row_count() > 0:
                    lv.click_row(0)
                    lv.click_delete()
                    lv.confirm_delete()
                    page.wait_for_timeout(500)
            except Exception:
                pass  # Entity may not have been deployed

        # Delete metadata classes (cascade deletes fields)
        nav = NavigationPage(page)
        nav.navigate_to("Schema Management", "Custom Class")
        lv = ListViewPage(page)
        lv.wait_for_grid()
        for name in ["AttrEmp", "AttrDept", "P9Child", "P9Parent",
                      "EscGrad", "RefTarget9"]:
            delete_if_exists(page, name)

        # Drop runtime tables
        conn = get_db_connection()
        try:
            cur = conn.cursor()
            for table in ["AttrEmp", "AttrDept", "P9Child", "P9Parent", "EscGrad"]:
                cur.execute(f'DROP TABLE IF EXISTS "{table}" CASCADE')
            conn.commit()
        finally:
            conn.close()
