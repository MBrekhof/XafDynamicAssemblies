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
