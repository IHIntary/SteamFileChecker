using SteamKit2.Authentication;

namespace SteamFileChecker.Desktop;

/// <summary>
/// Steam Guard for password sign-in, answered through the page: mobile-app approval (preferred when the account allows it),
/// or a code from the mobile authenticator or from email, which the page asks the user to type.
/// </summary>
internal sealed class PageAuthenticator(Func<string, string?, bool, Task<string>> askCode, Action<string> notify) : IAuthenticator
{
    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) => askCode("device", null, previousCodeWasIncorrect);

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) => askCode("email", email, previousCodeWasIncorrect);

    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        notify("请在手机 Steam 应用中批准这次登录。");
        return Task.FromResult(true);
    }
}
