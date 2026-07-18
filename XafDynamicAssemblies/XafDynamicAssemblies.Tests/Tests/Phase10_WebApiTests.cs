using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using XafDynamicAssemblies.Tests.Fixtures;
using XafDynamicAssemblies.Tests.Helpers;
using XafDynamicAssemblies.Tests.Pages;

namespace XafDynamicAssemblies.Tests.Tests;

/// <summary>
/// Phase 10 Tests: Web API (OData) endpoints for runtime and compiled entities.
/// Ported from tests/tests/test_phase10_web_api.py.
///
/// Verifies that Swagger UI/swagger.json are accessible, metadata entities (CustomClass,
/// CustomField) are always exposed via OData, runtime entities with IsApiExposed=true get
/// OData endpoints after Deploy (and IsApiExposed=false do not), full CRUD works through
/// OData, OData query features ($filter/$select/$top/$orderby/$count/$skip) work, and
/// toggling IsApiExposed + redeploying adds/removes endpoints.
/// </summary>
[Collection("Sequential")]
public class Phase10_WebApiTests : IAsyncLifetime
{
    private readonly BrowserFixture _fixture;
    private IPage _page = null!;

    // Own HttpClient per Python semantics (test_phase10_web_api.py uses bare `requests.get(...,
    // verify=False)` calls — no session reuse). SSL bypass mirrors `verify=False`.
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static string ApiBase => $"{TestSettings.BaseUrl}/api/odata";

    // Cross-test state — mirrors Python's TestODataCRUD._created_id class attribute.
    private static string? _createdId;

    public Phase10_WebApiTests(BrowserFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _page = await _fixture.NewPageAsync();

    public async Task DisposeAsync() => await _page.Context.DisposeAsync();

    // ============================================================
    // Helpers (mirror Python module-level functions)
    // ============================================================

    private async Task<(NavigationPage Nav, ListViewPage Lv)> NavToCustomClassAsync()
    {
        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        return (nav, lv);
    }

    private async Task DeleteIfExistsAsync(string text)
    {
        var lv = new ListViewPage(_page);
        if (await lv.HasRowWithTextAsync(text))
        {
            await lv.SelectRowWithTextAsync(text);
            await lv.ClickDeleteAsync();
            await lv.ConfirmDeleteAsync();
            await _page.WaitForTimeoutAsync(500);
        }
    }

    private async Task CreateClassViaUiAsync(string className, string navGroup, string description = "")
    {
        await NavToCustomClassAsync();
        await DeleteIfExistsAsync(className);

        var lv = new ListViewPage(_page);
        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(2000);
        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Class Name", className);
        await detail.FillFieldAsync("Navigation Group", navGroup);
        if (!string.IsNullOrEmpty(description))
            await detail.FillFieldAsync("Description", description);
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(2000);

        var nav = new NavigationPage(_page);
        await nav.NavigateToAsync("Schema Management", "Custom Class");
        lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
    }

    /// <summary>Wraps an OData HTTP response, mirroring Python's requests.Response usage (r.json()).</summary>
    private sealed class ApiResponse
    {
        public required HttpStatusCode StatusCode { get; init; }
        public required string Body { get; init; }

        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
    }

    private static string Truncate(string s) => s.Length > 500 ? s[..500] : s;

    /// <summary>GET an OData endpoint. Mirrors Python's api_get().</summary>
    private static async Task<ApiResponse> ApiGetAsync(
        string path, IDictionary<string, string>? queryParams = null, bool expectSuccess = true)
    {
        var url = path.StartsWith("http", StringComparison.Ordinal) ? path : $"{ApiBase}/{path}";
        if (queryParams is { Count: > 0 })
        {
            var qs = string.Join("&", queryParams.Select(
                kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            url = $"{url}?{qs}";
        }
        var response = await Http.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        if (expectSuccess)
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"GET {url} returned {(int)response.StatusCode}: {Truncate(body)}");
        return new ApiResponse { StatusCode = response.StatusCode, Body = body };
    }

    /// <summary>POST to an OData endpoint. Mirrors Python's api_post().</summary>
    private static async Task<ApiResponse> ApiPostAsync(string path, object data)
    {
        var url = $"{ApiBase}/{path}";
        var response = await Http.PostAsJsonAsync(url, data);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"POST {url} returned {(int)response.StatusCode}: {Truncate(body)}");
        return new ApiResponse { StatusCode = response.StatusCode, Body = body };
    }

    /// <summary>PATCH an OData entity by key. Mirrors Python's api_patch().</summary>
    private static async Task<ApiResponse> ApiPatchAsync(string path, string key, object data)
    {
        var url = $"{ApiBase}/{path}({key})";
        var response = await Http.PatchAsJsonAsync(url, data);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"PATCH {url} returned {(int)response.StatusCode}: {Truncate(body)}");
        return new ApiResponse { StatusCode = response.StatusCode, Body = body };
    }

    /// <summary>DELETE an OData entity by key. Mirrors Python's api_delete().</summary>
    private static async Task<ApiResponse> ApiDeleteAsync(string path, string key)
    {
        var url = $"{ApiBase}/{path}({key})";
        var response = await Http.DeleteAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"DELETE {url} returned {(int)response.StatusCode}: {Truncate(body)}");
        return new ApiResponse { StatusCode = response.StatusCode, Body = body };
    }

    /// <summary>
    /// Fetch a property by name, falling back to its camelCase form — mirrors Python's
    /// repeated `item.get("ProductName") or item.get("productName")` defensive pattern
    /// (the OData JSON casing isn't asserted anywhere, so tests tolerate either).
    /// </summary>
    private static JsonElement? GetProp(JsonElement el, string pascalName)
    {
        if (el.TryGetProperty(pascalName, out var v)) return v;
        var camelName = char.ToLowerInvariant(pascalName[0]) + pascalName[1..];
        if (camelName != pascalName && el.TryGetProperty(camelName, out v)) return v;
        return null;
    }

    /// <summary>Mirrors Python's `data.get("ID") or data.get("id") or data.get("Id")`.</summary>
    private static string? GetEntityId(JsonElement el)
    {
        foreach (var name in new[] { "ID", "id", "Id" })
            if (el.TryGetProperty(name, out var v))
                return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        return null;
    }

    private static IEnumerable<string> Keys(JsonElement el) =>
        el.ValueKind == JsonValueKind.Object ? el.EnumerateObject().Select(p => p.Name) : [];

    // ============================================================
    // TestSwaggerEndpoint: Swagger UI and swagger.json are accessible
    // ============================================================

    /// <summary>swagger.json should return valid JSON with API info.</summary>
    [Fact]
    public async Task Test_01_SwaggerJsonAccessible()
    {
        var response = await Http.GetAsync($"{TestSettings.BaseUrl}/swagger/v1/swagger.json");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"swagger.json returned {(int)response.StatusCode}");
        var data = JsonDocument.Parse(body).RootElement;
        Assert.True(data.TryGetProperty("info", out var info), "swagger.json should contain 'info' section");
        Assert.Equal("XafDynamicAssemblies API", info.GetProperty("title").GetString());
    }

    /// <summary>Swagger UI page should load.</summary>
    [Fact]
    public async Task Test_02_SwaggerUiAccessible()
    {
        await _page.GotoAsync($"{TestSettings.BaseUrl}/swagger",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30_000 });
        await _page.WaitForTimeoutAsync(2000);
        Assert.True(await _page.Locator("#swagger-ui, .swagger-ui").CountAsync() > 0,
            "Swagger UI should be rendered");
    }

    // ============================================================
    // TestMetadataEntityEndpoints: CustomClass/CustomField always exposed via OData
    // ============================================================

    /// <summary>GET /api/odata/CustomClass should return OData response.</summary>
    [Fact]
    public async Task Test_03_CustomClassEndpointExists()
    {
        var r = await ApiGetAsync("CustomClass");
        Assert.True(r.Json.TryGetProperty("value", out var value),
            $"OData response should have 'value' array. Got: {string.Join(", ", Keys(r.Json))}");
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
    }

    /// <summary>CustomClass endpoint should return valid OData structure with value array.</summary>
    [Fact]
    public async Task Test_04_CustomClassReturnsValidOdata()
    {
        var r = await ApiGetAsync("CustomClass");
        Assert.True(r.Json.TryGetProperty("value", out _), "Should have 'value' key in OData response");
        Assert.True(r.Json.TryGetProperty("@odata.context", out _),
            "Should have '@odata.context' in OData response");
    }

    /// <summary>GET /api/odata/CustomField should return OData response.</summary>
    [Fact]
    public async Task Test_05_CustomFieldEndpointExists()
    {
        var r = await ApiGetAsync("CustomField");
        Assert.True(r.Json.TryGetProperty("value", out var value), "OData response should have 'value' array");
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
    }

    /// <summary>OData $metadata endpoint should return EDM model.</summary>
    [Fact]
    public async Task Test_06_ODataMetadataEndpoint()
    {
        var response = await Http.GetAsync($"{ApiBase}/$metadata");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"$metadata returned {(int)response.StatusCode}");
        Assert.True(body.ToLowerInvariant().Contains("edmx") || body.Contains("EntityType"),
            "$metadata should contain EDM model definition");
    }

    // ============================================================
    // TestApiExposedSetup: create runtime entity w/ IsApiExposed=true, deploy, verify
    // ============================================================

    /// <summary>Create ApiProduct class for API testing.</summary>
    [Fact]
    public async Task Test_07_CreateApiEntity()
    {
        await CreateClassViaUiAsync("ApiProduct", "API Test", "Product entity for Web API tests");
    }

    /// <summary>Add fields to ApiProduct.</summary>
    [Fact]
    public Task Test_08_AddFields()
    {
        DatabaseHelper.InsertFieldViaDb("ApiProduct", "ProductName", "System.String", isDefault: true);
        DatabaseHelper.InsertFieldViaDb("ApiProduct", "Price", "System.Decimal");
        DatabaseHelper.InsertFieldViaDb("ApiProduct", "InStock", "System.Boolean");
        DatabaseHelper.InsertFieldViaDb("ApiProduct", "Quantity", "System.Int32");
        return Task.CompletedTask;
    }

    /// <summary>Set IsApiExposed=true on ApiProduct.</summary>
    [Fact]
    public Task Test_09_SetApiExposed()
    {
        DatabaseHelper.SetApiExposedViaDb("ApiProduct", true);

        using var conn = DatabaseHelper.GetConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT \"IsApiExposed\" FROM \"CustomClasses\" WHERE \"ClassName\" = @name " +
            "AND (\"GCRecord\" IS NULL OR \"GCRecord\" = 0)", conn);
        cmd.Parameters.AddWithValue("name", "ApiProduct");
        var result = cmd.ExecuteScalar();
        Assert.True(result is true, "IsApiExposed should be True in DB");
        return Task.CompletedTask;
    }

    /// <summary>Create ApiInternal class with IsApiExposed=false (should NOT get endpoint).</summary>
    [Fact]
    public async Task Test_10_CreateNonApiEntity()
    {
        await CreateClassViaUiAsync("ApiInternal", "API Test", "Internal entity - not API exposed");
        DatabaseHelper.InsertFieldViaDb("ApiInternal", "InternalName", "System.String", isDefault: true);
        DatabaseHelper.InsertFieldViaDb("ApiInternal", "Secret", "System.String");
        // Explicitly NOT setting IsApiExposed (defaults to false)
    }

    /// <summary>Deploy schema and restart to activate Web API endpoints.</summary>
    [Fact]
    public async Task Test_11_DeployAndRestart()
    {
        await NavToCustomClassAsync();
        await ServerHelper.ClickDeploySchemaAsync(_page);
        await ServerHelper.WaitForDeployRestartAsync(_page);
    }

    // ============================================================
    // TestRuntimeEntityODataEndpoints: OData endpoints for API-exposed runtime entities
    // ============================================================

    /// <summary>GET /api/odata/ApiProduct should return OData response.</summary>
    [Fact]
    public async Task Test_12_ApiProductEndpointExists()
    {
        var r = await ApiGetAsync("ApiProduct");
        Assert.True(r.Json.TryGetProperty("value", out var value),
            $"ApiProduct OData response should have 'value'. Got: {string.Join(", ", Keys(r.Json))}");
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
    }

    /// <summary>GET /api/odata/ApiInternal should NOT return OData JSON (not API-exposed).</summary>
    [Fact]
    public async Task Test_13_NonExposedEntityNotInOData()
    {
        var response = await Http.GetAsync($"{ApiBase}/ApiInternal");
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "";
        var isOData = contentType.Contains("application/json") || contentType.Contains("odata");
        if (response.StatusCode == HttpStatusCode.OK && isOData)
        {
            var body = await response.Content.ReadAsStringAsync();
            var data = JsonDocument.Parse(body).RootElement;
            Assert.False(data.TryGetProperty("value", out _),
                "ApiInternal should NOT have OData endpoints (not API-exposed)");
        }
        // If status is 404 or HTML, that's also acceptable
    }

    /// <summary>ApiProduct should appear in OData $metadata EDM model.</summary>
    [Fact]
    public async Task Test_14_ApiProductInMetadata()
    {
        var response = await Http.GetAsync($"{ApiBase}/$metadata");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ApiProduct", body);
    }

    /// <summary>ApiInternal should NOT have an EntitySet in OData $metadata (no CRUD endpoints).</summary>
    [Fact]
    public async Task Test_15_NonExposedNoEntitySetInMetadata()
    {
        var response = await Http.GetAsync($"{ApiBase}/$metadata");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("EntitySet Name=\"ApiInternal\"", body);
    }

    // ============================================================
    // TestODataCRUD: full CRUD operations via OData on runtime entity ApiProduct
    // ============================================================

    /// <summary>POST /api/odata/ApiProduct should create a new record.</summary>
    [Fact]
    public async Task Test_16_CreateViaPost()
    {
        var r = await ApiPostAsync("ApiProduct", new
        {
            ProductName = "ODataWidget",
            Price = 29.99,
            InStock = true,
            Quantity = 100,
        });
        Assert.True(GetProp(r.Json, "ProductName").HasValue,
            $"Response should contain created entity. Keys: {string.Join(", ", Keys(r.Json))}");
        var entityId = GetEntityId(r.Json);
        Assert.False(string.IsNullOrEmpty(entityId), $"Created entity should have an ID. Data: {r.Body}");
        _createdId = entityId;
    }

    /// <summary>GET /api/odata/ApiProduct should include the created record.</summary>
    [Fact]
    public async Task Test_17_ReadViaGet()
    {
        var r = await ApiGetAsync("ApiProduct");
        var names = r.Json.GetProperty("value").EnumerateArray()
            .Select(item => GetProp(item, "ProductName")?.GetString())
            .ToList();
        Assert.Contains("ODataWidget", names);
    }

    /// <summary>GET /api/odata/ApiProduct(key) should return the specific record.</summary>
    [Fact]
    public async Task Test_18_ReadSingleByKey()
    {
        Assert.False(string.IsNullOrEmpty(_createdId), "No created ID from test_16");
        var r = await ApiGetAsync($"ApiProduct({_createdId})");
        var name = GetProp(r.Json, "ProductName")?.GetString();
        Assert.Equal("ODataWidget", name);
    }

    /// <summary>PATCH /api/odata/ApiProduct(key) should update fields.</summary>
    [Fact]
    public async Task Test_19_UpdateViaPatch()
    {
        Assert.False(string.IsNullOrEmpty(_createdId), "No created ID from test_16");
        await ApiPatchAsync("ApiProduct", _createdId!, new { Price = 39.99, Quantity = 200 });

        var r = await ApiGetAsync($"ApiProduct({_createdId})");
        var price = GetProp(r.Json, "Price");
        var qty = GetProp(r.Json, "Quantity");
        Assert.True(price.HasValue);
        Assert.True(qty.HasValue);
        Assert.Equal(39.99, price!.Value.GetDouble());
        Assert.Equal(200, qty!.Value.GetInt32());
    }

    /// <summary>DELETE /api/odata/ApiProduct(key) should remove the record.</summary>
    [Fact]
    public async Task Test_20_DeleteViaDelete()
    {
        Assert.False(string.IsNullOrEmpty(_createdId), "No created ID from test_16");
        await ApiDeleteAsync("ApiProduct", _createdId!);

        var response = await Http.GetAsync($"{ApiBase}/ApiProduct({_createdId})");
        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent or HttpStatusCode.BadRequest,
            $"Deleted entity should return 404. Got {(int)response.StatusCode}");
    }

    // ============================================================
    // TestODataQueryFeatures: $filter, $select, $top, $orderby, $count, $skip
    // ============================================================

    /// <summary>Create multiple records for query testing.</summary>
    [Fact]
    public async Task Test_21_SeedTestData()
    {
        var seedData = new (string Name, double Price, int Qty, bool Stock)[]
        {
            ("QueryWidget1", 10.00, 50, true),
            ("QueryWidget2", 25.50, 100, true),
            ("QueryWidget3", 99.99, 5, false),
            ("QueryGadget1", 15.00, 200, true),
            ("QueryGadget2", 75.00, 0, false),
        };
        foreach (var (name, price, qty, stock) in seedData)
        {
            await ApiPostAsync("ApiProduct", new { ProductName = name, Price = price, Quantity = qty, InStock = stock });
        }
    }

    /// <summary>$filter on ProductName should return matching records.</summary>
    [Fact]
    public async Task Test_22_FilterByString()
    {
        var r = await ApiGetAsync("ApiProduct",
            new Dictionary<string, string> { ["$filter"] = "contains(ProductName, 'Widget')" });
        var names = r.Json.GetProperty("value").EnumerateArray()
            .Select(item => GetProp(item, "ProductName")?.GetString())
            .ToList();
        Assert.True(names.All(n => n != null && n.Contains("Widget")),
            $"All results should contain 'Widget'. Got: {string.Join(", ", names)}");
        Assert.True(names.Count >= 3, $"Should have at least 3 Widget records. Got: {names.Count}");
    }

    /// <summary>$filter on Price comparison should work.</summary>
    [Fact]
    public async Task Test_23_FilterByNumber()
    {
        var r = await ApiGetAsync("ApiProduct", new Dictionary<string, string> { ["$filter"] = "Price gt 50" });
        foreach (var item in r.Json.GetProperty("value").EnumerateArray())
        {
            var price = GetProp(item, "Price")!.Value.GetDouble();
            Assert.True(price > 50, $"All prices should be > 50. Got: {price}");
        }
    }

    /// <summary>$filter on InStock should work.</summary>
    [Fact]
    public async Task Test_24_FilterByBoolean()
    {
        var r = await ApiGetAsync("ApiProduct", new Dictionary<string, string> { ["$filter"] = "InStock eq true" });
        foreach (var item in r.Json.GetProperty("value").EnumerateArray())
        {
            var stock = GetProp(item, "InStock");
            Assert.True(stock is { ValueKind: JsonValueKind.True },
                $"All records should have InStock=true. Got: {stock}");
        }
    }

    /// <summary>$select should return only requested fields.</summary>
    [Fact]
    public async Task Test_25_SelectSpecificFields()
    {
        var r = await ApiGetAsync("ApiProduct", new Dictionary<string, string> { ["$select"] = "ProductName,Price" });
        var value = r.Json.GetProperty("value");
        Assert.True(value.GetArrayLength() > 0, "Should have results");
        var first = value[0];
        var hasName = GetProp(first, "ProductName").HasValue;
        Assert.True(hasName, $"Should have ProductName in response. Keys: {string.Join(", ", Keys(first))}");
    }

    /// <summary>$top and $orderby should limit and sort results.</summary>
    [Fact]
    public async Task Test_26_TopAndOrderby()
    {
        var r = await ApiGetAsync("ApiProduct",
            new Dictionary<string, string> { ["$top"] = "3", ["$orderby"] = "Price desc" });
        var items = r.Json.GetProperty("value").EnumerateArray().ToList();
        Assert.True(items.Count <= 3, $"$top=3 should limit to 3 results. Got: {items.Count}");
        var prices = items.Select(item => GetProp(item, "Price")!.Value.GetDouble()).ToList();
        Assert.Equal(prices.OrderByDescending(p => p).ToList(), prices);
    }

    /// <summary>$count=true should include total count in response.</summary>
    [Fact]
    public async Task Test_27_Count()
    {
        var r = await ApiGetAsync("ApiProduct", new Dictionary<string, string> { ["$count"] = "true" });
        JsonElement? count = r.Json.TryGetProperty("@odata.count", out var c1) ? c1
            : r.Json.TryGetProperty("@count", out var c2) ? c2 : null;
        Assert.True(count.HasValue, "Count should be present in response");
        Assert.True(count!.Value.GetInt64() >= 5, $"Count should be >= 5 (seeded records). Got: {count}");
    }

    /// <summary>$skip should offset results.</summary>
    [Fact]
    public async Task Test_28_Skip()
    {
        var rAll = await ApiGetAsync("ApiProduct", new Dictionary<string, string> { ["$orderby"] = "ProductName" });
        var allData = rAll.Json.GetProperty("value").EnumerateArray().ToList();

        var rSkip = await ApiGetAsync("ApiProduct",
            new Dictionary<string, string> { ["$skip"] = "2", ["$orderby"] = "ProductName" });
        var skipData = rSkip.Json.GetProperty("value").EnumerateArray().ToList();

        if (allData.Count > 2)
        {
            var firstSkipName = GetProp(skipData[0], "ProductName")?.GetString();
            var thirdAllName = GetProp(allData[2], "ProductName")?.GetString();
            Assert.Equal(thirdAllName, firstSkipName);
        }
    }

    // ============================================================
    // TestApiExposedToggle: toggling IsApiExposed and redeploying
    // ============================================================

    /// <summary>Set IsApiExposed=false on ApiProduct and redeploy.</summary>
    [Fact]
    public async Task Test_29_DisableApiForProduct()
    {
        DatabaseHelper.SetApiExposedViaDb("ApiProduct", false);
        await NavToCustomClassAsync();
        await ServerHelper.ClickDeploySchemaAsync(_page);
        await ServerHelper.WaitForDeployRestartAsync(_page);
    }

    /// <summary>ApiProduct should NOT have OData endpoint after disabling IsApiExposed.</summary>
    [Fact]
    public async Task Test_30_EndpointRemovedAfterDisable()
    {
        var response = await Http.GetAsync($"{ApiBase}/ApiProduct");
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "";
        var isOData = contentType.Contains("application/json") || contentType.Contains("odata");
        if (response.StatusCode == HttpStatusCode.OK && isOData)
        {
            var body = await response.Content.ReadAsStringAsync();
            var data = JsonDocument.Parse(body).RootElement;
            Assert.False(data.TryGetProperty("value", out _),
                "ApiProduct should NOT have OData endpoints after disabling");
        }
        // 404 or Blazor HTML fallback are both acceptable
    }

    /// <summary>CustomClass and CustomField endpoints should still work.</summary>
    [Fact]
    public async Task Test_31_MetadataEntitiesStillWork()
    {
        var r1 = await ApiGetAsync("CustomClass");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var r2 = await ApiGetAsync("CustomField");
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
    }

    /// <summary>Set IsApiExposed=true again and redeploy.</summary>
    [Fact]
    public async Task Test_32_ReEnableApiForProduct()
    {
        DatabaseHelper.SetApiExposedViaDb("ApiProduct", true);
        await NavToCustomClassAsync();
        await ServerHelper.ClickDeploySchemaAsync(_page);
        await ServerHelper.WaitForDeployRestartAsync(_page);
    }

    /// <summary>ApiProduct endpoint should be accessible again.</summary>
    [Fact]
    public async Task Test_33_EndpointRestoredAfterEnable()
    {
        var r = await ApiGetAsync("ApiProduct");
        Assert.True(r.Json.TryGetProperty("value", out _), "ApiProduct should be accessible after re-enabling");
    }

    // ============================================================
    // TestApiAndUIConsistency: data created via API appears in XAF UI and vice versa
    // ============================================================

    /// <summary>Record created via OData POST should appear in XAF ListView.</summary>
    [Fact]
    public async Task Test_34_CreateViaApiVisibleInUi()
    {
        await ApiPostAsync("ApiProduct", new { ProductName = "ApiCreatedItem", Price = 42.00, InStock = true, Quantity = 7 });

        await ServerHelper.ReloadAndWaitAsync(_page);
        await _page.GotoAsync($"{TestSettings.BaseUrl}/ApiProduct_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(3000);
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();
        Assert.True(await lv.HasRowWithTextAsync("ApiCreatedItem"),
            "Record created via API should appear in XAF ListView");
    }

    /// <summary>Record created via XAF UI should appear in OData GET.</summary>
    [Fact]
    public async Task Test_35_CreateViaUiVisibleInApi()
    {
        await ServerHelper.ReloadAndWaitAsync(_page);
        await _page.GotoAsync($"{TestSettings.BaseUrl}/ApiProduct_ListView",
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await _page.WaitForTimeoutAsync(3000);
        var lv = new ListViewPage(_page);
        await lv.WaitForGridAsync();

        await lv.ClickNewAsync();
        await _page.WaitForTimeoutAsync(2000);
        var detail = new DetailViewPage(_page);
        await detail.FillFieldAsync("Product Name", "UICreatedItem");
        await detail.ClickSaveAsync();
        await _page.WaitForTimeoutAsync(2000);

        var r = await ApiGetAsync("ApiProduct",
            new Dictionary<string, string> { ["$filter"] = "ProductName eq 'UICreatedItem'" });
        Assert.True(r.Json.GetProperty("value").GetArrayLength() >= 1,
            $"UICreatedItem should appear via API. Got: {r.Json.GetProperty("value")}");
    }

    // ============================================================
    // TestCleanup: remove all test data created by Phase 10
    // ============================================================

    /// <summary>Delete all test records via API and DB.</summary>
    [Fact]
    public async Task Test_99_CleanupApiRecords()
    {
        try
        {
            var r = await ApiGetAsync("ApiProduct");
            if (r.StatusCode == HttpStatusCode.OK)
            {
                foreach (var item in r.Json.GetProperty("value").EnumerateArray())
                {
                    var entityId = GetEntityId(item);
                    if (!string.IsNullOrEmpty(entityId))
                    {
                        try { await ApiDeleteAsync("ApiProduct", entityId); }
                        catch (Xunit.Sdk.XunitException) { /* matches Python's except AssertionError: pass */ }
                    }
                }
            }
        }
        catch
        {
            // ponytail: matches Python's bare `except Exception: pass` — cleanup is best-effort.
        }

        // Clean up metadata classes via DB
        foreach (var className in new[] { "ApiProduct", "ApiInternal" })
        {
            try { DatabaseHelper.DeleteCustomClass(className); }
            catch { /* best-effort cleanup */ }
        }

        // Deploy to clean up (removes runtime types from compilation)
        try
        {
            await NavToCustomClassAsync();
            await ServerHelper.ClickDeploySchemaAsync(_page);
            await ServerHelper.WaitForDeployRestartAsync(_page);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
