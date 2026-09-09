using System.Net.Http.Headers;
using System.Net.Http.Json;
using XafDynamicAssemblies.Tests.Pages;

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
        // TEST-005: wait until a NEW process answers, i.e. /_instance differs from the value
        // captured by ClickDeploySchemaAsync. The old fixed sleeps passed against the old
        // process whenever DDL + Roslyn took longer than ~7 s (the "cold-start artifacts").
        var deadline = DateTime.UtcNow.AddSeconds(serverTimeoutSeconds);
        while (true)
        {
            var now = await GetInstanceIdAsync();
            if (now != null && now != _instanceBeforeDeploy) break;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Server did not restart within {serverTimeoutSeconds}s (instance still {now ?? "unreachable"})");
            await Task.Delay(1000);
        }
        await WaitForServerAsync(serverTimeoutSeconds);
        await GotoRootToleratingRedirectAsync(page);
        await new LoginPage(page).EnsureLoggedInAsync();
        await page.WaitForSelectorAsync(".xaf-nav-link", new() { Timeout = 60_000 });
        await page.WaitForTimeoutAsync(3000);
    }

    /// <summary>
    /// SEC-004: obtain a Web API bearer token for the seeded Admin user via
    /// POST /api/Authentication/Authenticate and install it on <paramref name="client"/>.
    /// </summary>
    public static async Task AuthenticateHttpClientAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            $"{TestSettings.BaseUrl}/api/Authentication/Authenticate",
            new { userName = TestSettings.AdminUser, password = TestSettings.AdminPassword });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadAsStringAsync()).Trim('"');
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
    private static string? _instanceBeforeDeploy;

    /// <summary>GET /_instance (Startup.cs); null while the server is down or restarting.</summary>
    public static async Task<string?> GetInstanceIdAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var response = await Http.GetAsync($"{TestSettings.BaseUrl}/_instance", cts.Token);
            if (!response.IsSuccessStatusCode) return null;
            var id = (await response.Content.ReadAsStringAsync(cts.Token)).Trim().Trim('"');
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch { return null; }
    }

    public static async Task ClickDeploySchemaAsync(IPage page)
    {
        _instanceBeforeDeploy = await GetInstanceIdAsync();
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
        await new LoginPage(page).EnsureLoggedInAsync();
        await page.WaitForSelectorAsync(".xaf-nav-link", new() { Timeout = 60_000 });
        await page.WaitForTimeoutAsync(3000);
    }
}
