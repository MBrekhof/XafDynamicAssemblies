namespace XafDynamicAssemblies.Tests.Pages;

/// <summary>
/// XAF Blazor logon page (SEC-004). The form is an ordinary XAF DetailView of
/// AuthenticationStandardLogonParameters, so the editors carry the same
/// data-item-name markers as any other DetailView (the marker is the caption:
/// "User Name" / "Password", verified against DX 26.1 DOM); the button is the "Log In"
/// SimpleAction (data-action-name = caption, see BasePage.ActionButtonSelector).
/// </summary>
public class LoginPage
{
    private readonly IPage _page;

    public LoginPage(IPage page) => _page = page;

    public ILocator UserNameInput => _page.Locator(
        ".dxbl-fl-ctrl:has([data-item-name='User Name']) input:not([type='hidden']), " +
        "input[name='UserName'], #UserName").First;

    public ILocator PasswordInput => _page.Locator(
        ".dxbl-fl-ctrl:has([data-item-name='Password']) input:not([type='hidden']), " +
        "input[type='password']").First;

    public ILocator LoginButton => _page.Locator(
        "dxbl-toolbar-item > button[data-action-name='Log In'], dxbl-bar-item > button[data-action-name='Log In'], " +
        "button:has-text('Log In'), button[type='submit']").First;

    /// <summary>True when the logon form is on screen within <paramref name="timeout"/> ms.</summary>
    public async Task<bool> IsVisibleAsync(float timeout = 5000)
    {
        try
        {
            await UserNameInput.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = timeout });
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (PlaywrightException) { return false; }
    }

    public async Task LoginAsync(string user, string password)
    {
        await UserNameInput.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        await UserNameInput.FillAsync(user);
        await PasswordInput.FillAsync(password);
        await LoginButton.ClickAsync();
    }

    /// <summary>Log in if the logon form is showing; no-op when already authenticated.</summary>
    public async Task EnsureLoggedInAsync()
    {
        if (await IsVisibleAsync())
            await LoginAsync(TestSettings.AdminUser, TestSettings.AdminPassword);
    }
}
