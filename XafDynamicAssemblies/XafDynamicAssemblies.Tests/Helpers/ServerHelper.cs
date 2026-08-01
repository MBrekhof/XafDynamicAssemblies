namespace XafDynamicAssemblies.Tests.Helpers;

/// <summary>
/// Server polling and deploy/restart waiting, ported from the Python test suite's
/// wait_for_server()/wait_for_deploy_restart()/reload_and_wait()/click_deploy_schema()
/// helpers (duplicated across tests/tests/test_phase*.py — behavior is identical in every copy).
/// </summary>
public static class ServerHelper
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    });

    /// <summary>Poll until the server responds (handles the process-restart window).</summary>
    public static async Task WaitForServerAsync(int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var response = await Http.GetAsync(TestSettings.BaseUrl, cts.Token);
                if ((int)response.StatusCode < 500)
                    return;
            }
            catch { /* server not up yet */ }
            await Task.Delay(1000);
        }
        throw new TimeoutException($"Server not responsive after {timeoutSeconds}s");
    }

    /// <summary>
    /// Wait for the Deploy Schema + process-level restart cycle. The server exits with code 42
    /// and a wrapper script restarts it as a fresh process, so this uses generous timeouts.
    /// Most phases wait up to 60s for the server to come back; the bulk-compile phase (Phase 8)
    /// needs up to 90s, hence the overridable <paramref name="serverTimeoutSeconds"/>.
    /// </summary>
    public static async Task WaitForDeployRestartAsync(IPage page, int serverTimeoutSeconds = 60)
    {
        await page.WaitForTimeoutAsync(5000);      // Let the deploy action process
        await Task.Delay(5000);                    // Server is briefly down during process restart
        await WaitForServerAsync(serverTimeoutSeconds);
        await GotoRootToleratingRedirectAsync(page);
        await page.WaitForSelectorAsync(".xaf-nav-link", new() { Timeout = 60_000 });
        await page.WaitForTimeoutAsync(3000);
    }

    /// <summary>
    /// Navigate to the app root, tolerating the post-restart auto-redirect race (TEST-001):
    /// the reconnecting Blazor circuit can fire its own navigation at the same moment (XAF
    /// restores the last shortcut, e.g. /CustomClass_ListView), aborting ours with
    /// "Navigation to ... is interrupted by another navigation". That interruption proves the
    /// app is alive, so wait out the competing navigation and retry; the .xaf-nav-link wait
    /// that callers do next is the real readiness gate.
    /// </summary>
    private static async Task GotoRootToleratingRedirectAsync(IPage page)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await page.GotoAsync(TestSettings.BaseUrl,
                    new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
                return;
            }
            catch (PlaywrightException ex) when (
                ex.Message.Contains("interrupted by another navigation") && attempt < 2)
            {
                await page.WaitForTimeoutAsync(2000);
            }
        }
    }

    /// <summary>Click the 'Deploy Schema' toolbar action, then dismiss the confirmation dialog if present.</summary>
    public static async Task ClickDeploySchemaAsync(IPage page)
    {
        // data-action-name holds the Action's rendered Caption ("Deploy Schema" —
        // SchemaChangeController.cs), not its Id ("DeploySchema"). See BasePage.cs
        // ActionButtonSelector remarks for the DX 26.1 source citation. Text-based locator
        // kept as a last-resort fallback in case the DOM shape changes again.
        var deployBtn = page.Locator("dxbl-toolbar-item > button[data-action-name=\"Deploy Schema\"], dxbl-bar-item > button[data-action-name=\"Deploy Schema\"]");
        if (await deployBtn.CountAsync() == 0)
            deployBtn = page.Locator("button:has-text('Deploy Schema'), span:has-text('Deploy Schema')");
        await deployBtn.First.ClickAsync();
        await page.WaitForTimeoutAsync(1000);

        var confirmBtn = page.Locator("button:has-text('Yes'), button:has-text('OK')");
        if (await confirmBtn.CountAsync() > 0)
            await confirmBtn.First.ClickAsync();
    }

    /// <summary>
    /// Full navigation back to the app root + wait for XAF navigation, tolerating brief
    /// downtime. Used after non-restart-triggering changes. Note: this navigates to BaseUrl
    /// (matching Python's reload_and_wait, which uses page.goto) rather than reloading the
    /// current page — there is no page.reload()-based helper anywhere in the Python suite.
    /// </summary>
    public static async Task ReloadAndWaitAsync(IPage page)
    {
        await WaitForServerAsync(30);
        await GotoRootToleratingRedirectAsync(page);
        await page.WaitForSelectorAsync(".xaf-nav-link", new() { Timeout = 60_000 });
        await page.WaitForTimeoutAsync(3000);
    }
}
