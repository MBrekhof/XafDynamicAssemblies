using XafDynamicAssemblies.Tests.Fixtures;

namespace XafDynamicAssemblies.Tests.Tests;

[Collection("Sequential")]
public class SmokeTest
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
