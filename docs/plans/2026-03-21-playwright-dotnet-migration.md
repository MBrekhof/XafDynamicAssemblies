# Playwright .NET Test Migration Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Migrate the 104 Playwright E2E tests from Python to .NET (C# / xUnit), eliminating the Python Docker container and unifying the tech stack.

**Architecture:** A new `XafDynamicAssemblies.Tests` xUnit project in the solution, using `Microsoft.Playwright` for browser automation. Page objects mirror the existing Python ones. The Flask mock LLM server becomes an ASP.NET Core minimal API hosted in-process. Tests run sequentially (shared server) with ordered execution within each phase.

**Tech Stack:** .NET 8, xUnit, Microsoft.Playwright, Npgsql (direct DB access), ASP.NET Core minimal API (mock LLM), HttpClient (API tests)

---

## Source Reference

The Python tests being migrated live in `tests/`. Key files:

| Python File | Lines | Purpose |
|---|---|---|
| `conftest.py` | 74 | Browser/page fixtures, mock LLM startup |
| `pages/base_page.py` | 49 | Common XAF Blazor interactions |
| `pages/navigation_page.py` | 136 | Accordion nav with JS click workaround |
| `pages/list_view_page.py` | 66 | DxGrid row interactions |
| `pages/detail_view_page.py` | 80 | Form field fill/read/checkbox |
| `pages/ai_chat_page.py` | 208 | DxAIChat component interactions |
| `mock_llm/server.py` | 296 | Flask mock LLM (Anthropic + OpenAI wire formats) |
| `mock_llm/scripts.py` | 246 | Deterministic scripted responses |
| `tests/test_phase1_*.py` | 170 | 11 metadata CRUD tests |
| `tests/test_phase2_*.py` | 369 | 13 runtime entity tests |
| `tests/test_phase3_*.py` | 260 | 9 validation tests |
| `tests/test_phase4_*.py` | 276 | 7 hot-load tests |
| `tests/test_phase5_*.py` | 338 | 8 relationship tests |
| `tests/test_phase6_*.py` | 323 | 9 graduation tests |
| `tests/test_phase7_*.py` | 362 | 7 error handling tests |
| `tests/test_phase8_*.py` | 233 | 4 performance tests |
| `tests/test_phase9_*.py` | 623 | 21 review fixes tests |
| `tests/test_phase10_*.py` | 643 | 36 Web API tests |
| `tests/test_phase11_*_mocked.py` | 345 | 33 mocked AI chat tests |
| `tests/test_phase11_*_live.py` | 261 | 5 live AI chat tests |

---

## Target Project Structure

```
XafDynamicAssemblies/
  XafDynamicAssemblies.Tests/
    XafDynamicAssemblies.Tests.csproj
    GlobalUsings.cs
    TestSettings.cs                    # Env var config (BASE_URL, DB, etc.)
    TestOrder.cs                       # ITestCaseOrderer for test_01_ ordering
    Fixtures/
      BrowserFixture.cs                # Session-scoped Chromium via IAsyncLifetime
      MockLlmFixture.cs                # In-process ASP.NET Core mock LLM
    Pages/
      BasePage.cs
      NavigationPage.cs
      ListViewPage.cs
      DetailViewPage.cs
      AIChatPanel.cs
    Helpers/
      DatabaseHelper.cs                # Npgsql: connection, insert_field, query
      ServerHelper.cs                  # WaitForServer, DeployAndRestart
    MockLlm/
      MockLlmServer.cs                 # ASP.NET Core minimal API
      ScriptMatcher.cs                 # Deterministic response matching
    Tests/
      Phase01_MetadataCrudTests.cs
      Phase02_RuntimeEntityTests.cs
      Phase03_ValidationTests.cs
      Phase04_HotLoadTests.cs
      Phase05_RelationshipTests.cs
      Phase06_GraduationTests.cs
      Phase07_ErrorHandlingTests.cs
      Phase08_PerformanceTests.cs
      Phase09_ReviewFixesTests.cs
      Phase10_WebApiTests.cs
      Phase11_AIChatMockedTests.cs
      Phase11_AIChatLiveTests.cs
```

---

## Key Design Decisions

### Test Ordering

Python tests rely on execution order within each phase (test_01 creates data, test_02 reads it). xUnit doesn't guarantee order by default. Solution:

- Custom `ITestCaseOrderer` that sorts by method name (alphabetical = numeric order)
- `[TestCaseOrderer("...", "...")]` attribute on each test class
- `[assembly: CollectionBehavior(DisableTestParallelization = true)]` — all tests run sequentially since they share one server

### State Sharing Between Tests

Python classes share state via class attributes. xUnit creates new instances per test. Solution:

- Static fields on test classes for cross-test state (entity IDs, created records)
- `IClassFixture<BrowserFixture>` for shared browser instance
- `IClassFixture<MockLlmFixture>` for mock LLM (Phase 11 only)

### Page Object Translation

Python `async` → C# `async/await` is nearly 1:1. Key mappings:

| Python (Playwright) | C# (Microsoft.Playwright) |
|---|---|
| `page.locator(sel)` | `page.Locator(sel)` |
| `page.wait_for_selector(sel)` | `page.WaitForSelectorAsync(sel)` |
| `page.wait_for_timeout(ms)` | `page.WaitForTimeoutAsync(ms)` |
| `page.evaluate(js)` | `page.EvaluateAsync(js)` |
| `locator.click()` | `locator.ClickAsync()` |
| `locator.fill(val)` | `locator.FillAsync(val)` |
| `locator.inner_text()` | `locator.InnerTextAsync()` |
| `locator.input_value()` | `locator.InputValueAsync()` |
| `locator.is_visible()` | `locator.IsVisibleAsync()` |
| `locator.count()` | `locator.CountAsync()` |
| `expect(locator).to_be_visible()` | `Expect(locator).ToBeVisibleAsync()` |

### Database Access

Replace `psycopg2` with `Npgsql` — same PostgreSQL wire protocol, native .NET.

### HTTP Client for API Tests

Replace Python `requests` with `HttpClient` — same REST calls, native .NET.

### Mock LLM Server

Replace Flask with ASP.NET Core minimal API. Same endpoints, same scripted responses, hosted in-process via `WebApplication` on a background thread.

---

## Tasks

### Task 1: Create Test Project and Install Dependencies

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/XafDynamicAssemblies.Tests.csproj`
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/GlobalUsings.cs`
- Modify: `XafDynamicAssemblies.slnx`

**Step 1: Create the project**

```bash
cd XafDynamicAssemblies
dotnet new xunit -n XafDynamicAssemblies.Tests -o XafDynamicAssemblies/XafDynamicAssemblies.Tests
dotnet sln XafDynamicAssemblies.slnx add XafDynamicAssemblies/XafDynamicAssemblies.Tests/XafDynamicAssemblies.Tests.csproj
```

**Step 2: Add NuGet packages**

```bash
cd XafDynamicAssemblies/XafDynamicAssemblies.Tests
dotnet add package Microsoft.Playwright --version 1.49.0
dotnet add package Npgsql --version 8.0.6
dotnet add package Microsoft.AspNetCore.App  # for mock LLM server (use FrameworkReference)
```

Edit the `.csproj` to use `FrameworkReference` for ASP.NET Core (needed for the mock LLM minimal API):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.*" />
    <PackageReference Include="Microsoft.Playwright" Version="1.49.0" />
    <PackageReference Include="Npgsql" Version="8.0.6" />
    <PackageReference Include="xunit" Version="2.9.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.*" />
  </ItemGroup>
</Project>
```

**Step 3: Install Playwright browsers**

```bash
pwsh XafDynamicAssemblies/XafDynamicAssemblies.Tests/bin/Debug/net8.0/playwright.ps1 install chromium
```

**Step 4: Create GlobalUsings.cs**

```csharp
global using Xunit;
global using Microsoft.Playwright;
```

**Step 5: Verify build**

```bash
dotnet build XafDynamicAssemblies.slnx
```

Expected: Build succeeds, default xUnit template test passes.

**Step 6: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Tests/ XafDynamicAssemblies.slnx
git commit -m "feat: scaffold xUnit + Playwright .NET test project"
```

---

### Task 2: Test Configuration and Ordering Infrastructure

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/TestSettings.cs`
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/TestOrder.cs`

**Step 1: Create TestSettings.cs**

Port all environment variable defaults from Python `conftest.py` and test files:

```csharp
namespace XafDynamicAssemblies.Tests;

public static class TestSettings
{
    public static string BaseUrl =>
        Environment.GetEnvironmentVariable("BASE_URL") ?? "https://localhost:5001";

    public static bool Headless =>
        bool.TryParse(Environment.GetEnvironmentVariable("HEADLESS"), out var h) ? h : true;

    public static int SlowMo =>
        int.TryParse(Environment.GetEnvironmentVariable("SLOW_MO"), out var s) ? s : 0;

    public static int MockLlmPort =>
        int.TryParse(Environment.GetEnvironmentVariable("MOCK_LLM_PORT"), out var p) ? p : 5555;

    public static string DbHost =>
        Environment.GetEnvironmentVariable("DB_HOST") ?? "localhost";

    public static int DbPort =>
        int.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out var p) ? p : 5434;

    public static string DbName =>
        Environment.GetEnvironmentVariable("DB_NAME") ?? "XafDynamicAssemblies";

    public static string DbUser =>
        Environment.GetEnvironmentVariable("DB_USER") ?? "xafdynamic";

    public static string DbPassword =>
        Environment.GetEnvironmentVariable("DB_PASS") ?? "xafdynamic";

    public static string? AiTestApiKey =>
        Environment.GetEnvironmentVariable("AI_TEST_API_KEY");
}
```

**Step 2: Create TestOrder.cs**

Custom orderer that sorts test methods alphabetically (matching `test_01_`, `test_02_` pattern):

```csharp
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
[assembly: TestCaseOrderer(
    "XafDynamicAssemblies.Tests.AlphabeticalOrderer",
    "XafDynamicAssemblies.Tests")]

namespace XafDynamicAssemblies.Tests;

public class AlphabeticalOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(
        IEnumerable<TTestCase> testCases) where TTestCase : ITestCase
    {
        return testCases.OrderBy(tc => tc.TestMethod.Method.Name);
    }
}
```

**Step 3: Verify build**

```bash
dotnet build XafDynamicAssemblies/XafDynamicAssemblies.Tests
```

**Step 4: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Tests/
git commit -m "feat: add test configuration and alphabetical test ordering"
```

---

### Task 3: Browser Fixture

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Fixtures/BrowserFixture.cs`

**Step 1: Create BrowserFixture.cs**

Port the session-scoped `browser` and function-scoped `context`/`page` fixtures from `conftest.py`:

```csharp
namespace XafDynamicAssemblies.Tests.Fixtures;

public class BrowserFixture : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public IBrowser Browser => _browser;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new()
        {
            Headless = TestSettings.Headless,
            SlowMo = TestSettings.SlowMo,
        });
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    /// <summary>
    /// Creates a fresh browser context and page, navigates to the app,
    /// and waits for XAF to fully load. Call once per test method.
    /// </summary>
    public async Task<IPage> NewPageAsync()
    {
        var context = await _browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 1920, Height = 1080 },
            IgnoreHTTPSErrors = true,
        });
        context.SetDefaultTimeout(30_000);

        var page = await context.NewPageAsync();
        await page.GotoAsync(TestSettings.BaseUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".xaf-nav-link", new() { Timeout = 60_000 });
        await page.WaitForTimeoutAsync(2000); // Roslyn cold start buffer
        return page;
    }
}

/// <summary>
/// xUnit collection definition so all test classes share one BrowserFixture instance.
/// </summary>
[CollectionDefinition("Sequential")]
public class SequentialCollection : ICollectionFixture<BrowserFixture> { }
```

**Step 2: Write a smoke test to verify the fixture works**

Create `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/SmokeTest.cs`:

```csharp
namespace XafDynamicAssemblies.Tests.Tests;

[Collection("Sequential")]
public class SmokeTest : IClassFixture<BrowserFixture>
{
    private readonly BrowserFixture _browser;
    public SmokeTest(BrowserFixture browser) => _browser = browser;

    [Fact(Skip = "Manual — requires running server")]
    public async Task Server_Loads_XAF_Navigation()
    {
        var page = await _browser.NewPageAsync();
        var navLinks = page.Locator(".xaf-nav-link");
        Assert.True(await navLinks.CountAsync() > 0);
        await page.Context.DisposeAsync();
    }
}
```

**Step 3: Verify build**

```bash
dotnet build XafDynamicAssemblies/XafDynamicAssemblies.Tests
```

**Step 4: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Tests/
git commit -m "feat: add BrowserFixture with session-scoped Chromium and per-test page"
```

---

### Task 4: Helper Classes (Database + Server)

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Helpers/DatabaseHelper.cs`
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Helpers/ServerHelper.cs`

**Step 1: Create DatabaseHelper.cs**

Port `get_db_connection()` and `insert_field_via_db()` from multiple Python test files:

```csharp
using Npgsql;

namespace XafDynamicAssemblies.Tests.Helpers;

public static class DatabaseHelper
{
    public static NpgsqlConnection GetConnection()
    {
        var connStr = $"Host={TestSettings.DbHost};Port={TestSettings.DbPort};" +
                      $"Database={TestSettings.DbName};Username={TestSettings.DbUser};" +
                      $"Password={TestSettings.DbPassword}";
        var conn = new NpgsqlConnection(connStr);
        conn.Open();
        return conn;
    }

    public static void InsertFieldViaDb(
        string className, string fieldName, string typeName,
        bool isDefault = false, string? referencedClassName = null,
        string? attributes = null)
    {
        using var conn = GetConnection();
        // Find the CustomClass ID
        using var findCmd = new NpgsqlCommand(
            "SELECT \"Id\" FROM \"CustomClass\" WHERE \"ClassName\" = @name", conn);
        findCmd.Parameters.AddWithValue("name", className);
        var classId = (Guid?)findCmd.ExecuteScalar()
            ?? throw new Exception($"CustomClass '{className}' not found");

        // Insert the field
        using var insertCmd = new NpgsqlCommand(@"
            INSERT INTO ""CustomField"" (""Id"", ""CustomClassId"", ""FieldName"", ""TypeName"",
                ""IsDefaultField"", ""ReferencedClassName"", ""Attributes"")
            VALUES (@id, @classId, @fieldName, @typeName, @isDefault, @refClass, @attrs)", conn);
        insertCmd.Parameters.AddWithValue("id", Guid.NewGuid());
        insertCmd.Parameters.AddWithValue("classId", classId);
        insertCmd.Parameters.AddWithValue("fieldName", fieldName);
        insertCmd.Parameters.AddWithValue("typeName", typeName);
        insertCmd.Parameters.AddWithValue("isDefault", isDefault);
        insertCmd.Parameters.AddWithValue("refClass", (object?)referencedClassName ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("attrs", (object?)attributes ?? DBNull.Value);
    }

    public static bool TableExists(string tableName)
    {
        using var conn = GetConnection();
        using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_name = @name", conn);
        cmd.Parameters.AddWithValue("name", tableName);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    public static bool ForeignKeyExists(string tableName, string columnName)
    {
        using var conn = GetConnection();
        using var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*) FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name
            WHERE tc.constraint_type = 'FOREIGN KEY'
              AND tc.table_name = @table
              AND kcu.column_name = @col", conn);
        cmd.Parameters.AddWithValue("table", tableName);
        cmd.Parameters.AddWithValue("col", columnName);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    public static void DeleteCustomClass(string className)
    {
        using var conn = GetConnection();
        using var cmd = new NpgsqlCommand(@"
            DELETE FROM ""CustomField"" WHERE ""CustomClassId"" IN
                (SELECT ""Id"" FROM ""CustomClass"" WHERE ""ClassName"" = @name);
            DELETE FROM ""CustomClass"" WHERE ""ClassName"" = @name;", conn);
        cmd.Parameters.AddWithValue("name", className);
        cmd.ExecuteNonQuery();
    }
}
```

**Step 2: Create ServerHelper.cs**

Port `wait_for_server()` and `wait_for_deploy_restart()`:

```csharp
namespace XafDynamicAssemblies.Tests.Helpers;

public static class ServerHelper
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    });

    public static async Task WaitForServerAsync(int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await Http.GetAsync(TestSettings.BaseUrl);
                if ((int)response.StatusCode < 500)
                    return;
            }
            catch { /* server not up yet */ }
            await Task.Delay(1000);
        }
        throw new TimeoutException($"Server not responsive after {timeoutSeconds}s");
    }

    public static async Task WaitForDeployRestartAsync(IPage page)
    {
        await page.WaitForTimeoutAsync(5000);    // Let deploy process
        await Task.Delay(5000);                   // Server is down
        await WaitForServerAsync(60);             // Poll until back
        await page.GotoAsync(TestSettings.BaseUrl,
            new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await page.WaitForSelectorAsync(".xaf-nav-link", new() { Timeout = 60_000 });
        await page.WaitForTimeoutAsync(3000);
    }

    public static async Task ClickDeploySchemaAsync(IPage page)
    {
        var deployBtn = page.Locator("dxbl-toolbar-item[text='Deploy Schema']");
        await deployBtn.ClickAsync();
        await page.WaitForTimeoutAsync(500);

        // Handle confirmation dialog if present
        var yesBtn = page.Locator("text=Yes");
        if (await yesBtn.IsVisibleAsync())
            await yesBtn.ClickAsync();
    }

    public static async Task ReloadAndWaitAsync(IPage page)
    {
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".xaf-nav-link", new() { Timeout = 60_000 });
        await page.WaitForTimeoutAsync(2000);
    }
}
```

**Step 3: Verify build**

```bash
dotnet build XafDynamicAssemblies/XafDynamicAssemblies.Tests
```

**Step 4: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Tests/
git commit -m "feat: add DatabaseHelper and ServerHelper for test infrastructure"
```

---

### Task 5: Page Objects — BasePage, NavigationPage, ListViewPage, DetailViewPage

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Pages/BasePage.cs`
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Pages/NavigationPage.cs`
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Pages/ListViewPage.cs`
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Pages/DetailViewPage.cs`

**Step 1: Create BasePage.cs**

Direct port of `pages/base_page.py`:

```csharp
namespace XafDynamicAssemblies.Tests.Pages;

public class BasePage
{
    protected readonly IPage Page;

    public BasePage(IPage page) => Page = page;

    public async Task WaitForLoadingAsync(int timeout = 10_000)
    {
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle,
            new() { Timeout = timeout });
        await Page.WaitForTimeoutAsync(500);
    }

    public async Task ClickNewAsync()
    {
        await Page.Locator("dxbl-toolbar-item[text='New']").ClickAsync();
        await WaitForLoadingAsync();
    }

    public async Task ClickSaveAsync()
    {
        await Page.Locator("dxbl-toolbar-item[text='Save']").ClickAsync();
        await WaitForLoadingAsync();
    }

    public async Task ClickDeleteAsync()
    {
        await Page.Locator("dxbl-toolbar-item[text='Delete']").ClickAsync();
        await Page.WaitForTimeoutAsync(500);
    }

    public async Task ConfirmDeleteAsync()
    {
        var yesBtn = Page.Locator("text=Yes").Or(Page.Locator("text=OK"));
        await yesBtn.ClickAsync();
        await WaitForLoadingAsync();
    }

    public async Task ClickActionAsync(string actionText)
    {
        await Page.Locator($"dxbl-toolbar-item[text='{actionText}']").ClickAsync();
        await WaitForLoadingAsync();
    }
}
```

**Step 2: Create NavigationPage.cs**

Port `pages/navigation_page.py` — critical JS click workaround for Blazor:

```csharp
namespace XafDynamicAssemblies.Tests.Pages;

public class NavigationPage
{
    private readonly IPage _page;

    public NavigationPage(IPage page) => _page = page;

    public async Task<bool> ExpandGroupJsAsync(string group)
    {
        var result = await _page.EvaluateAsync<bool>(@"(groupName) => {
            const groups = document.querySelectorAll('dxbl-group-control.xaf-nav-item');
            for (const g of groups) {
                const header = g.querySelector('.dxbl-group-header');
                if (!header) continue;
                const text = header.textContent.trim();
                if (text === groupName) {
                    const expandBtn = g.querySelector('.dxbl-group-expand-btn');
                    if (expandBtn) {
                        expandBtn.click();
                        return true;
                    }
                    header.click();
                    return true;
                }
            }
            return false;
        }", group);

        if (result)
            await _page.WaitForTimeoutAsync(1500);
        return result;
    }

    public async Task NavigateToAsync(string group, string item)
    {
        await ExpandGroupJsAsync(group);

        var clicked = await _page.EvaluateAsync<bool>(@"(itemName) => {
            const items = document.querySelectorAll('.dxbl-accordion-item-content .xaf-nav-link');
            for (const el of items) {
                if (el.textContent.trim() === itemName) {
                    const clickArea = el.querySelector('.xaf-navigation-link-click-area')
                        || el.querySelector('.xaf-nav-link') || el;
                    clickArea.click();
                    return true;
                }
            }
            return false;
        }", item);

        if (!clicked)
            throw new InvalidOperationException($"Nav item '{item}' not found in group '{group}'");

        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _page.WaitForTimeoutAsync(500);
    }

    public async Task NavigateToItemAsync(string item)
    {
        // Try visible items first
        var clicked = await _page.EvaluateAsync<bool>(@"(itemName) => {
            const items = document.querySelectorAll('.xaf-nav-link');
            for (const el of items) {
                if (el.textContent.trim() === itemName) {
                    const clickArea = el.querySelector('.xaf-navigation-link-click-area') || el;
                    clickArea.click();
                    return true;
                }
            }
            return false;
        }", item);

        if (!clicked)
        {
            // Expand all groups and retry
            var groups = await _page.Locator("dxbl-group-control.xaf-nav-item").AllAsync();
            foreach (var group in groups)
            {
                var headerText = await group.Locator(".dxbl-group-header").InnerTextAsync();
                await ExpandGroupJsAsync(headerText.Trim());
            }

            clicked = await _page.EvaluateAsync<bool>(@"(itemName) => {
                const items = document.querySelectorAll('.xaf-nav-link');
                for (const el of items) {
                    if (el.textContent.trim() === itemName) {
                        const clickArea = el.querySelector('.xaf-navigation-link-click-area') || el;
                        clickArea.click();
                        return true;
                    }
                }
                return false;
            }", item);
        }

        if (!clicked)
            throw new InvalidOperationException($"Nav item '{item}' not found");

        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _page.WaitForTimeoutAsync(500);
    }

    public async Task<bool> IsGroupVisibleAsync(string group)
    {
        var locator = _page.Locator($"dxbl-group-control.xaf-nav-item >> text='{group}'");
        return await locator.CountAsync() > 0;
    }

    public async Task<bool> IsItemVisibleAsync(string item)
    {
        var locator = _page.Locator($".xaf-nav-link >> text='{item}'");
        return await locator.CountAsync() > 0;
    }
}
```

**Step 3: Create ListViewPage.cs**

Port `pages/list_view_page.py`:

```csharp
namespace XafDynamicAssemblies.Tests.Pages;

public class ListViewPage : BasePage
{
    private const string GridRow = ".dxbl-grid-table tbody tr[data-visible-index]";

    public ListViewPage(IPage page) : base(page) { }

    public async Task WaitForGridAsync(int timeout = 15_000)
    {
        await Page.WaitForFunctionAsync(@"() => {
            const grids = document.querySelectorAll('.dxbl-grid');
            return Array.from(grids).some(g => g.offsetWidth > 0);
        }", null, new() { Timeout = timeout });
        await Page.WaitForTimeoutAsync(500);
    }

    public async Task<int> GetRowCountAsync()
    {
        return await Page.Locator(GridRow).CountAsync();
    }

    public async Task ClickRowAsync(int index)
    {
        await Page.Locator(GridRow).Nth(index).ClickAsync();
        await Page.WaitForTimeoutAsync(300);
    }

    public async Task DoubleClickRowAsync(int index)
    {
        await Page.Locator(GridRow).Nth(index).DblClickAsync();
        await WaitForLoadingAsync();
    }

    public async Task<int> FindRowWithTextAsync(string text)
    {
        var rows = Page.Locator(GridRow);
        var count = await rows.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var rowText = await rows.Nth(i).InnerTextAsync();
            if (rowText.Contains(text, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    public async Task SelectRowWithTextAsync(string text)
    {
        var index = await FindRowWithTextAsync(text);
        if (index < 0) throw new InvalidOperationException($"Row with text '{text}' not found");
        await ClickRowAsync(index);
    }

    public async Task DoubleClickRowWithTextAsync(string text)
    {
        var index = await FindRowWithTextAsync(text);
        if (index < 0) throw new InvalidOperationException($"Row with text '{text}' not found");
        await DoubleClickRowAsync(index);
    }

    public async Task<bool> HasRowWithTextAsync(string text)
    {
        return await FindRowWithTextAsync(text) >= 0;
    }

    public async Task<bool> HasNoDataAsync()
    {
        var noData = Page.Locator("text=No data to display");
        return await noData.IsVisibleAsync();
    }
}
```

**Step 4: Create DetailViewPage.cs**

Port `pages/detail_view_page.py`:

```csharp
namespace XafDynamicAssemblies.Tests.Pages;

public class DetailViewPage : BasePage
{
    public DetailViewPage(IPage page) : base(page) { }

    private ILocator FindContainer(string label) =>
        Page.Locator($".dxbl-fl-ctrl:has([data-item-name='{label}'])");

    private ILocator FindInput(string label)
    {
        var container = FindContainer(label);
        // Try non-hidden, non-checkbox input first
        var input = container.Locator("input:not([type='hidden']):not([type='checkbox'])");
        return input;
    }

    public async Task FillFieldAsync(string label, string value)
    {
        var input = FindInput(label);
        if (await input.CountAsync() == 0)
        {
            // Fall back to textarea
            input = FindContainer(label).Locator("textarea");
        }
        await input.ClickAsync();
        await input.FillAsync(value);
        await Page.Keyboard.PressAsync("Tab");
        await Page.WaitForTimeoutAsync(300);
    }

    public async Task ClearFieldAsync(string label)
    {
        var input = FindInput(label);
        await input.ClickAsync();
        await input.FillAsync("");
        await Page.Keyboard.PressAsync("Tab");
        await Page.WaitForTimeoutAsync(300);
    }

    public async Task<string> GetFieldValueAsync(string label)
    {
        var input = FindInput(label);
        return await input.InputValueAsync();
    }

    public async Task<string> GetFieldTextAsync(string label)
    {
        var container = FindContainer(label);
        var input = container.Locator("input:not([type='hidden'])");
        if (await input.CountAsync() > 0)
            return await input.InputValueAsync();
        return (await container.InnerTextAsync()).Trim();
    }

    public async Task SetCheckboxAsync(string label, bool isChecked)
    {
        var checkbox = FindContainer(label).Locator("input[type='checkbox']");
        var current = await checkbox.IsCheckedAsync();
        if (current != isChecked)
        {
            await checkbox.ClickAsync();
            await Page.WaitForTimeoutAsync(300);
        }
    }
}
```

**Step 5: Verify build**

```bash
dotnet build XafDynamicAssemblies/XafDynamicAssemblies.Tests
```

**Step 6: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Tests/Pages/
git commit -m "feat: port all page objects (Base, Navigation, ListView, DetailView)"
```

---

### Task 6: AI Chat Page Object

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Pages/AIChatPanel.cs`

**Step 1: Create AIChatPanel.cs**

Port `pages/ai_chat_page.py` (208 lines). This is the largest page object.

```csharp
namespace XafDynamicAssemblies.Tests.Pages;

public class AIChatPanel
{
    private readonly IPage _page;
    private readonly NavigationPage _nav;

    private const string ChatContainer = ".copilot-chat-container";
    private const string MessageInput = ".copilot-chat textarea";
    private const string SendButton = ".copilot-chat .dxai-chat-button-send, .copilot-chat button[aria-label='Send']";
    private const string AllMessages = ".copilot-chat .dxai-chat-message";
    private const string AssistantMessages = ".copilot-chat .dxai-chat-message-assistant";
    private const string MessageContent = ".dxai-chat-message-content";
    private const string LoadingIndicator = ".copilot-chat .dxai-chat-message-typing, .copilot-chat .dxai-chat-typing-indicator";
    private const string SuggestionButton = ".copilot-chat .dxai-chat-prompt-suggestion";
    private const string EmptyArea = ".copilot-empty-area";

    public AIChatPanel(IPage page)
    {
        _page = page;
        _nav = new NavigationPage(page);
    }

    public async Task<bool> IsVisibleAsync() =>
        await _page.Locator(ChatContainer).IsVisibleAsync();

    public async Task WaitForPanelAsync(int timeout = 10_000)
    {
        await _page.Locator(ChatContainer)
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = timeout });
        await _page.WaitForTimeoutAsync(500);
    }

    public async Task NavigateToChatAsync()
    {
        try
        {
            await _nav.NavigateToAsync("Schema Management", "AI Chat");
        }
        catch
        {
            await _page.GotoAsync($"{TestSettings.BaseUrl}/AIChatView",
                new() { WaitUntil = WaitUntilState.NetworkIdle });
        }
        await WaitForPanelAsync();
    }

    public async Task SendMessageAsync(string text, int timeout = 30_000)
    {
        var input = _page.Locator(MessageInput);
        await input.FillAsync(text);
        await _page.WaitForTimeoutAsync(200);

        var sendBtn = _page.Locator(SendButton);
        if (await sendBtn.IsVisibleAsync())
            await sendBtn.ClickAsync();
        else
            await _page.Keyboard.PressAsync("Enter");

        await _page.WaitForTimeoutAsync(500);
        await WaitForResponseAsync(timeout);
    }

    public async Task WaitForResponseAsync(int timeout = 30_000)
    {
        await _page.Locator(AssistantMessages).First
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = timeout });

        // Wait for loading indicator to disappear
        var loading = _page.Locator(LoadingIndicator);
        if (await loading.IsVisibleAsync())
        {
            await loading.WaitForAsync(new()
            {
                State = WaitForSelectorState.Hidden,
                Timeout = timeout
            });
        }
        await _page.WaitForTimeoutAsync(500);
    }

    public async Task<string> GetLastResponseAsync()
    {
        var messages = _page.Locator(AssistantMessages);
        var count = await messages.CountAsync();
        if (count == 0) return "";
        return (await messages.Nth(count - 1)
            .Locator(MessageContent).InnerTextAsync()).Trim();
    }

    public async Task<string> GetLastResponseHtmlAsync()
    {
        var messages = _page.Locator(AssistantMessages);
        var count = await messages.CountAsync();
        if (count == 0) return "";
        return await messages.Nth(count - 1)
            .Locator(MessageContent).InnerHTMLAsync();
    }

    public async Task<List<string>> GetAllResponsesAsync()
    {
        var messages = _page.Locator(AssistantMessages);
        var count = await messages.CountAsync();
        var result = new List<string>();
        for (var i = 0; i < count; i++)
            result.Add((await messages.Nth(i).Locator(MessageContent).InnerTextAsync()).Trim());
        return result;
    }

    public async Task<int> GetMessageCountAsync() =>
        await _page.Locator(AllMessages).CountAsync();

    public async Task ClickSuggestionAsync(string text)
    {
        var clicked = await _page.EvaluateAsync<bool>(@"(text) => {
            const buttons = document.querySelectorAll('.dxai-chat-prompt-suggestion');
            for (const btn of buttons) {
                if (btn.textContent.includes(text)) {
                    btn.click();
                    return true;
                }
            }
            return false;
        }", text);

        if (!clicked)
            throw new InvalidOperationException($"Suggestion button with text '{text}' not found");
    }

    public async Task<bool> HasTableInLastResponseAsync()
    {
        var messages = _page.Locator(AssistantMessages);
        var count = await messages.CountAsync();
        if (count == 0) return false;
        var table = messages.Nth(count - 1).Locator("table");
        return await table.CountAsync() > 0;
    }

    public async Task<bool> ResponseContainsAsync(string text)
    {
        var response = await GetLastResponseAsync();
        return response.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> IsEmptyStateVisibleAsync() =>
        await _page.Locator(EmptyArea).IsVisibleAsync();
}
```

**Step 2: Verify build**

```bash
dotnet build XafDynamicAssemblies/XafDynamicAssemblies.Tests
```

**Step 3: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Tests/Pages/AIChatPanel.cs
git commit -m "feat: port AIChatPanel page object"
```

---

### Task 7: Mock LLM Server

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/MockLlm/ScriptMatcher.cs`
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/MockLlm/MockLlmServer.cs`
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Fixtures/MockLlmFixture.cs`

**Step 1: Create ScriptMatcher.cs**

Port `mock_llm/scripts.py` — deterministic response matching:

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XafDynamicAssemblies.Tests.MockLlm;

public class ScriptMatcher
{
    private Dictionary<string, object>? _pendingEntity;

    public void Reset() => _pendingEntity = null;

    public Dictionary<string, object> Match(string userMessage)
    {
        var msg = userMessage.ToLowerInvariant();

        // Confirm pending entity
        if (_pendingEntity != null && IsConfirmation(msg))
        {
            var entity = _pendingEntity;
            _pendingEntity = null;
            return ToolUse("create_entity", entity);
        }

        if (msg.Contains("list") && msg.Contains("entit"))
            return ToolUse("list_entities", new { });

        if (msg.Contains("list") && msg.Contains("role"))
            return ToolUse("list_roles", new { });

        if ((msg.Contains("describe") || msg.Contains("show")) && msg.Contains("field"))
            return ToolUse("describe_entity", new { entity_name = ExtractEntityName(userMessage) });

        if (msg.Contains("pending") || msg.Contains("changes"))
            return ToolUse("get_pending_changes", new { });

        if (msg.Contains("add") && msg.Contains("field"))
            return Text($"I'll add a field '{ExtractFieldName(userMessage)}'. Please confirm.");

        if (msg.Contains("delete") || msg.Contains("remove"))
            return Text("I'll delete that entity. Please confirm by saying 'yes'.");

        if (msg.Contains("permission") || msg.Contains("access"))
            return Text("Could you clarify which role and entity you'd like to configure?");

        if (msg.Contains("validate") || msg.Contains("compile"))
            return ToolUse("validate_schema", new { });

        if (msg.Contains("create"))
        {
            var name = ExtractEntityName(userMessage);
            _pendingEntity = new Dictionary<string, object>
            {
                ["class_name"] = name,
                ["navigation_group"] = "Default",
                ["fields"] = new[] {
                    new { field_name = "Name", type_name = "System.String" },
                    new { field_name = "Description", type_name = "System.String" }
                }
            };
            return Text($"I'll create entity '{name}' with fields Name (string) and Description (string). Shall I proceed?");
        }

        return Text("I can help you create, modify, or delete entities. What would you like to do?");
    }

    public Dictionary<string, object>? MatchToolResult(string toolName)
    {
        return Text($"Done! The {toolName} operation completed successfully.");
    }

    private static bool IsConfirmation(string msg) =>
        msg.Contains("yes") || msg.Contains("confirm") || msg.Contains("proceed")
        || msg.Contains("looks good") || msg == "y";

    private static string ExtractEntityName(string text)
    {
        // Try quoted: "create 'Foo'"
        var quoted = Regex.Match(text, @"[""'](\w+)[""']");
        if (quoted.Success) return quoted.Groups[1].Value;
        // Try "create Foo entity" or "create entity Foo"
        var named = Regex.Match(text, @"create\s+(?:an?\s+)?(\w+)", RegexOptions.IgnoreCase);
        if (named.Success) return named.Groups[1].Value;
        return "UnknownEntity";
    }

    private static string ExtractFieldName(string text)
    {
        var match = Regex.Match(text, @"field\s+(\w+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : "UnknownField";
    }

    private static Dictionary<string, object> Text(string content) =>
        new() { ["type"] = "text", ["content"] = content };

    private static Dictionary<string, object> ToolUse(string name, object input) =>
        new() { ["type"] = "tool_use", ["name"] = name, ["input"] = input };
}
```

**Step 2: Create MockLlmServer.cs**

Port `mock_llm/server.py` — ASP.NET Core minimal API:

```csharp
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace XafDynamicAssemblies.Tests.MockLlm;

public class MockLlmServer
{
    private readonly WebApplication _app;
    private readonly ScriptMatcher _matcher = new();
    private int _idCounter;

    public MockLlmServer(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        _app.MapGet("/health", () => Results.Json(new { status = "ok" }));
        _app.MapPost("/reset", () => { _matcher.Reset(); _idCounter = 0; return Results.Ok(); });
        _app.MapPost("/v1/messages", HandleAnthropicAsync);
        _app.MapPost("/v1/chat/completions", HandleOpenAIAsync);
    }

    public Task StartAsync() => _app.StartAsync();
    public Task StopAsync() => _app.StopAsync();

    private async Task<IResult> HandleAnthropicAsync(HttpContext ctx)
    {
        var body = await JsonDocument.ParseAsync(ctx.Request.Body);
        var userMsg = ExtractLastUserMessageAnthropic(body);
        var toolResult = HasToolResultAnthropic(body);

        Dictionary<string, object> match;
        if (toolResult != null)
            match = _matcher.MatchToolResult(toolResult) ?? ScriptMatcher_TextFallback();
        else if (userMsg != null)
            match = _matcher.Match(userMsg);
        else
            match = ScriptMatcher_TextFallback();

        var id = $"mock-{++_idCounter}";
        if (match["type"].ToString() == "tool_use")
        {
            return Results.Json(new
            {
                id,
                type = "message",
                role = "assistant",
                content = new[] {
                    new {
                        type = "tool_use",
                        id = $"call_{Guid.NewGuid():N}"[..16],
                        name = match["name"],
                        input = match["input"]
                    }
                },
                stop_reason = "tool_use"
            });
        }

        return Results.Json(new
        {
            id,
            type = "message",
            role = "assistant",
            content = new[] { new { type = "text", text = match["content"] } },
            stop_reason = "end_turn"
        });
    }

    private async Task<IResult> HandleOpenAIAsync(HttpContext ctx)
    {
        var body = await JsonDocument.ParseAsync(ctx.Request.Body);
        var userMsg = ExtractLastUserMessageOpenAI(body);
        var toolResult = HasToolResultOpenAI(body);

        Dictionary<string, object> match;
        if (toolResult != null)
            match = _matcher.MatchToolResult(toolResult) ?? ScriptMatcher_TextFallback();
        else if (userMsg != null)
            match = _matcher.Match(userMsg);
        else
            match = ScriptMatcher_TextFallback();

        var id = $"mock-{++_idCounter}";
        if (match["type"].ToString() == "tool_use")
        {
            return Results.Json(new
            {
                id,
                @object = "chat.completion",
                choices = new[] {
                    new {
                        message = new {
                            role = "assistant",
                            content = (string?)null,
                            tool_calls = new[] {
                                new {
                                    id = $"call_{Guid.NewGuid():N}"[..16],
                                    type = "function",
                                    function = new {
                                        name = match["name"],
                                        arguments = JsonSerializer.Serialize(match["input"])
                                    }
                                }
                            }
                        },
                        finish_reason = "tool_calls"
                    }
                }
            });
        }

        return Results.Json(new
        {
            id,
            @object = "chat.completion",
            choices = new[] {
                new {
                    message = new {
                        role = "assistant",
                        content = match["content"].ToString(),
                        tool_calls = (object?)null
                    },
                    finish_reason = "stop"
                }
            }
        });
    }

    private static string? ExtractLastUserMessageAnthropic(JsonDocument body)
    {
        if (!body.RootElement.TryGetProperty("messages", out var messages)) return null;
        for (var i = messages.GetArrayLength() - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (msg.GetProperty("role").GetString() != "user") continue;
            var content = msg.GetProperty("content");
            if (content.ValueKind == JsonValueKind.String) return content.GetString();
            if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                    if (block.GetProperty("type").GetString() == "text")
                        return block.GetProperty("text").GetString();
            }
        }
        return null;
    }

    private static string? HasToolResultAnthropic(JsonDocument body)
    {
        if (!body.RootElement.TryGetProperty("messages", out var messages)) return null;
        for (var i = messages.GetArrayLength() - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (msg.GetProperty("role").GetString() != "user") continue;
            var content = msg.GetProperty("content");
            if (content.ValueKind != JsonValueKind.Array) continue;
            foreach (var block in content.EnumerateArray())
                if (block.GetProperty("type").GetString() == "tool_result")
                    return block.TryGetProperty("name", out var n) ? n.GetString() : "unknown";
        }
        return null;
    }

    private static string? ExtractLastUserMessageOpenAI(JsonDocument body)
    {
        if (!body.RootElement.TryGetProperty("messages", out var messages)) return null;
        for (var i = messages.GetArrayLength() - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (msg.GetProperty("role").GetString() == "user")
                return msg.GetProperty("content").GetString();
        }
        return null;
    }

    private static string? HasToolResultOpenAI(JsonDocument body)
    {
        if (!body.RootElement.TryGetProperty("messages", out var messages)) return null;
        for (var i = messages.GetArrayLength() - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (msg.GetProperty("role").GetString() == "tool")
                return msg.TryGetProperty("name", out var n) ? n.GetString() : "unknown";
        }
        return null;
    }

    private static Dictionary<string, object> ScriptMatcher_TextFallback() =>
        new() { ["type"] = "text", ["content"] = "OK" };
}
```

**Step 3: Create MockLlmFixture.cs**

```csharp
namespace XafDynamicAssemblies.Tests.Fixtures;

public class MockLlmFixture : IAsyncLifetime
{
    private MockLlm.MockLlmServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = new MockLlm.MockLlmServer(TestSettings.MockLlmPort);
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
    }
}
```

**Step 4: Verify build**

```bash
dotnet build XafDynamicAssemblies/XafDynamicAssemblies.Tests
```

**Step 5: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Tests/MockLlm/ XafDynamicAssemblies/XafDynamicAssemblies.Tests/Fixtures/MockLlmFixture.cs
git commit -m "feat: port mock LLM server from Flask to ASP.NET Core minimal API"
```

---

### Task 8: Migrate Phase 1 — Metadata CRUD (11 tests)

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/Phase01_MetadataCrudTests.cs`

**Step 1: Port all 11 tests**

Translate `test_phase1_metadata_crud.py` to C#. This is the simplest phase — pure UI CRUD on CustomClass and CustomField. Use it as the pattern for all subsequent phases.

Pattern: Each `test_NN_name` becomes `public async Task Test_NN_Name()`. Static fields hold cross-test state. Helper methods like `NavToCustomClass()` become private async methods.

Reference the Python source at `tests/tests/test_phase1_metadata_crud.py` for exact selectors, field names, and assertions.

**Step 2: Run the tests against a running server**

```bash
dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests --filter "FullyQualifiedName~Phase01"
```

Expected: All 11 tests pass (requires running server + database).

**Step 3: Commit**

```bash
git add XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/Phase01_MetadataCrudTests.cs
git commit -m "feat: migrate Phase 1 metadata CRUD tests to .NET Playwright"
```

---

### Task 9: Migrate Phase 2 — Runtime Entities (13 tests)

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/Phase02_RuntimeEntityTests.cs`

**Step 1: Port all 13 tests**

Key differences from Phase 1: uses `DatabaseHelper.InsertFieldViaDb()` for field creation, `ServerHelper.WaitForDeployRestartAsync()` for server restart, and direct URL navigation (`page.GotoAsync($"{BaseUrl}/Customer_ListView")`).

Reference: `tests/tests/test_phase2_runtime_entities.py`

**Step 2: Run and verify**

```bash
dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests --filter "FullyQualifiedName~Phase02"
```

**Step 3: Commit**

```bash
git commit -m "feat: migrate Phase 2 runtime entity tests to .NET Playwright"
```

---

### Task 10: Migrate Phase 3 — Validation (9 tests)

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/Phase03_ValidationTests.cs`

Port `test_phase3_validation.py`. Key helper: `TrySaveAndCheckValidation()` that detects validation error popups.

**Commit:** `git commit -m "feat: migrate Phase 3 validation tests to .NET Playwright"`

---

### Task 11: Migrate Phase 4 — Hot-Load (7 tests)

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/Phase04_HotLoadTests.cs`

Port `test_phase4_hot_load.py`. Heavy use of `ServerHelper.WaitForDeployRestartAsync()` and `ServerHelper.ClickDeploySchemaAsync()`.

**Commit:** `git commit -m "feat: migrate Phase 4 hot-load tests to .NET Playwright"`

---

### Task 12: Migrate Phase 5 — Relationships (8 tests)

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/Phase05_RelationshipTests.cs`

Port `test_phase5_relationships.py`. Uses `DatabaseHelper.ForeignKeyExists()` to verify FK constraints.

**Commit:** `git commit -m "feat: migrate Phase 5 relationship tests to .NET Playwright"`

---

### Task 13: Migrate Phase 6 — Graduation (9 tests)

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/Phase06_GraduationTests.cs`

Port `test_phase6_graduation.py`. Tests Graduate action, source generation, data preservation.

**Commit:** `git commit -m "feat: migrate Phase 6 graduation tests to .NET Playwright"`

---

### Task 14: Migrate Phase 7 — Error Handling (7 tests)

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/Phase07_ErrorHandlingTests.cs`

Port `test_phase7_error_handling.py`. Tests degraded mode, recovery from invalid metadata, empty metadata startup.

**Commit:** `git commit -m "feat: migrate Phase 7 error handling tests to .NET Playwright"`

---

### Task 15: Migrate Phase 8 — Performance (4 tests)

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/Phase08_PerformanceTests.cs`

Port `test_phase8_performance.py`. Bulk 10-class creation, concurrent page loads.

**Commit:** `git commit -m "feat: migrate Phase 8 performance tests to .NET Playwright"`

---

### Task 16: Migrate Phase 9 — Review Fixes (21 tests)

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/Phase09_ReviewFixesTests.cs`

Port `test_phase9_review_fixes.py` (623 lines — largest test file). Cross-references, required refs, field attributes, graduation escaping.

**Commit:** `git commit -m "feat: migrate Phase 9 review fixes tests to .NET Playwright"`

---

### Task 17: Migrate Phase 10 — Web API (36 tests)

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/Phase10_WebApiTests.cs`

Port `test_phase10_web_api.py` (643 lines). Uses `HttpClient` instead of Python `requests`. OData CRUD, query features, Swagger verification.

Key pattern change: Python `requests.get(url, verify=False)` → C# `HttpClient` with `HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }`.

**Commit:** `git commit -m "feat: migrate Phase 10 Web API tests to .NET Playwright"`

---

### Task 18: Migrate Phase 11 — AI Chat Mocked (33 tests)

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/Phase11_AIChatMockedTests.cs`

Port `test_phase11_ai_chat_mocked.py`. Uses `IClassFixture<MockLlmFixture>` to start the mock LLM server.

**Commit:** `git commit -m "feat: migrate Phase 11 mocked AI chat tests to .NET Playwright"`

---

### Task 19: Migrate Phase 11 — AI Chat Live (5 tests)

**Files:**
- Create: `XafDynamicAssemblies/XafDynamicAssemblies.Tests/Tests/Phase11_AIChatLiveTests.cs`

Port `test_phase11_ai_chat_live.py`. Uses `[Trait("Category", "LiveAI")]` instead of `@pytest.mark.live_ai`. Skip tests when `TestSettings.AiTestApiKey` is null.

```csharp
public class Phase11_AIChatLiveTests : IClassFixture<BrowserFixture>
{
    public Phase11_AIChatLiveTests(BrowserFixture browser)
    {
        Skip.If(TestSettings.AiTestApiKey == null, "AI_TEST_API_KEY not set");
        // ...
    }
}
```

Note: xUnit doesn't have built-in `Skip.If()`. Use the `Xunit.SkippableFact` NuGet package, or check in each test and return early.

**Commit:** `git commit -m "feat: migrate Phase 11 live AI chat tests to .NET Playwright"`

---

### Task 20: Remove Python Test Infrastructure

**Files:**
- Delete: `tests/conftest.py`
- Delete: `tests/pages/` (all files)
- Delete: `tests/tests/` (all files)
- Delete: `tests/mock_llm/` (all files)
- Delete: `tests/requirements.txt`
- Delete: `tests/pytest.ini`
- Delete: `tests/.pytest_cache/`
- Delete: `Dockerfile.python`
- Modify: `docker-compose.yml` — remove `python-tests` service
- Modify: `README.md` — update test instructions
- Modify: `CLAUDE.md` — update test references

**Step 1: Delete Python files**

```bash
git rm -r tests/
git rm Dockerfile.python
```

**Step 2: Update docker-compose.yml**

Remove the `python-tests` service block. Keep `postgres` service.

**Step 3: Update README.md**

Replace the "Running Tests" section:

```markdown
## Running Tests

The server must be running via `run-server.bat` / `run-server.sh` (not `dotnet run` directly) because tests trigger deploy+restart cycles.

```bash
# Install Playwright browsers (first time only)
pwsh XafDynamicAssemblies/XafDynamicAssemblies.Tests/bin/Debug/net8.0/playwright.ps1 install chromium

# Full regression
dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests -v normal --timeout 180000

# Single phase
dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests --filter "FullyQualifiedName~Phase04" -v normal

# Skip live AI tests (require API key)
dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests --filter "Category!=LiveAI"
```
```

**Step 4: Update CLAUDE.md**

Update file locations and test commands to reference the .NET project.

**Step 5: Commit**

```bash
git add -A
git commit -m "chore: remove Python test infrastructure, update docs for .NET Playwright"
```

---

### Task 21: Final Verification

**Step 1: Full regression run**

```bash
dotnet test XafDynamicAssemblies/XafDynamicAssemblies.Tests -v normal --timeout 180000
```

Expected: All 104 tests pass.

**Step 2: Verify clean build**

```bash
dotnet build XafDynamicAssemblies.slnx
```

**Step 3: Verify docker compose still works**

```bash
docker compose up -d postgres
```

(Only postgres — no more python-tests container needed.)

**Step 4: Commit any final fixes**

---

## Migration Checklist

| Component | Python | C# (.NET) | Status |
|---|---|---|---|
| Browser fixture | conftest.py | Fixtures/BrowserFixture.cs | |
| Navigation PO | pages/navigation_page.py | Pages/NavigationPage.cs | |
| ListView PO | pages/list_view_page.py | Pages/ListViewPage.cs | |
| DetailView PO | pages/detail_view_page.py | Pages/DetailViewPage.cs | |
| AI Chat PO | pages/ai_chat_page.py | Pages/AIChatPanel.cs | |
| Base PO | pages/base_page.py | Pages/BasePage.cs | |
| Mock LLM server | mock_llm/server.py | MockLlm/MockLlmServer.cs | |
| Mock LLM scripts | mock_llm/scripts.py | MockLlm/ScriptMatcher.cs | |
| DB helper | inline in tests | Helpers/DatabaseHelper.cs | |
| Server helper | inline in tests | Helpers/ServerHelper.cs | |
| Phase 1 (11) | test_phase1_*.py | Phase01_MetadataCrudTests.cs | |
| Phase 2 (13) | test_phase2_*.py | Phase02_RuntimeEntityTests.cs | |
| Phase 3 (9) | test_phase3_*.py | Phase03_ValidationTests.cs | |
| Phase 4 (7) | test_phase4_*.py | Phase04_HotLoadTests.cs | |
| Phase 5 (8) | test_phase5_*.py | Phase05_RelationshipTests.cs | |
| Phase 6 (9) | test_phase6_*.py | Phase06_GraduationTests.cs | |
| Phase 7 (7) | test_phase7_*.py | Phase07_ErrorHandlingTests.cs | |
| Phase 8 (4) | test_phase8_*.py | Phase08_PerformanceTests.cs | |
| Phase 9 (21) | test_phase9_*.py | Phase09_ReviewFixesTests.cs | |
| Phase 10 (36) | test_phase10_*.py | Phase10_WebApiTests.cs | |
| Phase 11 mocked (33) | test_phase11_*_mocked.py | Phase11_AIChatMockedTests.cs | |
| Phase 11 live (5) | test_phase11_*_live.py | Phase11_AIChatLiveTests.cs | |
| Dockerfile.python | Dockerfile.python | (removed) | |
| docker-compose | python-tests svc | (removed) | |
