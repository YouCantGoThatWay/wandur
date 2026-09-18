using L = Wandur.Core.Localization.Strings;
using Wandur.Core.Sessions;
using Wandur.Core.Protocol;
using Wandur.Core.Settings;
using Wandur.Desktop.Security;
using Wandur.Desktop.Services;

namespace Wandur.Desktop;

public sealed partial class WorkspaceController : IWorldProfileStore, IClientSettingsStore
{
    public IPasswordVault Passwords { get; }
    public IReadOnlyList<ConnectionProfile> Profiles => Settings.Profiles;
    public string CredentialStoreName => Passwords.Description;
    private LoginAttempt? _login;
    private bool _gmcpLoginHandled;
    // Track only server text received since the last input, so blank packets cannot replay old prompts.
    private string CurrentPrompt => _promptTerminal.Lines.LastOrDefault(line => !string.IsNullOrWhiteSpace(line.Text))?.Text ?? "";

    private sealed class LoginAttempt(ConnectionProfile profile, string password)
    {
        public ConnectionProfile Profile { get; } = profile;
        public string Password { get; } = password;
        public AutoLoginSequence Sequence { get; } = new(profile);
        public bool Sending { get; set; }
        public bool ProtocolLogin { get; set; }
    }

    public async Task SaveWorldAsync(ConnectionProfile profile, string password, bool rememberPassword)
    {
        var original = Settings.Profiles.FirstOrDefault(p => p.Id == profile.Id);
        string? createdKey = null;
        if (!rememberPassword) profile = profile with { PasswordId = null, AutoLogin = false };
        else if (password.Length > 0)
        {
            PasswordVault.ValidatePassword(password);
            profile = profile with { PasswordId = Guid.NewGuid() };
            createdKey = PasswordVault.Key(profile);
        }
        else if (original?.PasswordId is null || profile.PasswordId is null || PasswordVault.Key(original) != PasswordVault.Key(profile))
            throw new ArgumentException(L.EnterAPasswordToSaveForThisServerAnd);
        profile.Validate();
        if (createdKey is not null) await Passwords.WriteAsync(createdKey, password);
        try
        {
            var updated = Settings.Profiles.Select(p => p.Id == profile.Id ? profile : p).ToList();
            if (updated.All(p => p.Id != profile.Id)) updated.Add(profile);
            SaveSettings(Settings with { Profiles = updated });
        }
        catch
        {
            if (createdKey is not null)
            {
                try { await Passwords.DeleteAsync(createdKey); }
                catch { ShowNotice(L.WorldWasNotSavedAnUnusedPasswordMayRemain); }
            }
            throw;
        }
        if (original?.PasswordId is not null && (profile.PasswordId is null || PasswordVault.Key(original) != PasswordVault.Key(profile)))
            await ForgetPasswordAsync(original);
    }

    public async Task RemoveWorldAsync(ConnectionProfile profile)
    {
        SaveSettings(Settings with { Profiles = Settings.Profiles.Where(p => p.Id != profile.Id).ToList() });
        if (profile.PasswordId is not null) await ForgetPasswordAsync(profile);
    }

    private async Task ForgetPasswordAsync(ConnectionProfile profile)
    {
        try { await Passwords.DeleteAsync(PasswordVault.Key(profile)); }
        catch { ShowNotice(L.WorldSettingsSavedButTheOldPasswordCouldNot); }
    }

    private async Task<string?> PrepareLoginAsync(ConnectionProfile? profile, CancellationToken token)
    {
        _login = null; _gmcpLoginHandled = false;
        if (profile?.AutoLogin != true) return null;
        try
        {
            var password = await Passwords.ReadAsync(PasswordVault.Key(profile)).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (password is null) return L.SavedPasswordWasnTFoundLogInManuallyOr;
            PasswordVault.ValidatePassword(password);
            RememberDiagnosticSecret(password);
            _login = new(profile, password);
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return L.CouldNotReadTheSavedPasswordUnlockYourSystem; }
    }

    private async Task ReceiveGmcpLoginAsync(TelnetSession session, GmcpLoginMessage message)
    {
        if (!ReferenceEquals(_session, session) || !session.IsConnected) return;
        if (message.Kind == GmcpLoginKind.Result)
        {
            // A completed authentication must not be followed by a second automatic attempt.
            _gmcpLoginHandled = true;
            if (_login is null) return;
            _login = null; _promptTerminal.Clear();
            if (!message.Success) ShowNotice(L.GmcpLoginRejected);
            RefreshScriptState(); Changed?.Invoke();
            return;
        }
        if (_gmcpLoginHandled) return;
        _gmcpLoginHandled = true;
        var login = _login;
        var useCredentials = message.PasswordSupported && login is not null && !login.Sending &&
            !login.Sequence.Started && !login.Sequence.Expired;
        if (useCredentials)
        {
            login!.ProtocolLogin = true; login.Sending = true;
            _promptTerminal.Clear(); RefreshScriptState();
        }
        var token = _connectionCancellation?.Token ?? default;
        try
        {
            // An empty object explicitly hands control back to the server's text login screen.
            await session.SendLoginCredentialsAsync(useCredentials ? login!.Profile.Username : null,
                useCredentials ? login!.Password : null, token);
            if (useCredentials && ReferenceEquals(_session, session))
            {
                CommandsSent++;
                _ = ExpireGmcpLoginAsync(session, login!, token);
            }
        }
        catch
        {
            if (ReferenceEquals(_session, session) && useCredentials && ReferenceEquals(_login, login))
            { _login = null; ShowNotice(L.AutoLoginCouldNotSendCredentialsLogInManually); }
        }
        finally
        {
            if (useCredentials) login!.Sending = false;
            if (ReferenceEquals(_session, session)) { RefreshScriptState(); Changed?.Invoke(); }
        }
    }

    private async Task ExpireGmcpLoginAsync(TelnetSession session, LoginAttempt login, CancellationToken token)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), token); }
        catch (OperationCanceledException) { return; }
        if (!ReferenceEquals(_session, session) || !ReferenceEquals(_login, login)) return;
        _login = null;
        ShowNotice(L.GmcpLoginTimedOut);
        RefreshScriptState(); Changed?.Invoke();
    }

    private async Task TryAutoLoginAsync(IMudSession session)
    {
        var login = _login;
        if (login is null || login.Sending || login.ProtocolLogin || !ReferenceEquals(_session, session) || !session.IsConnected) return;
        // Only the current prompt is eligible, never earlier transcript text.
        var prompt = CurrentPrompt;
        LoginStep step;
        try { step = login.Sequence.Next(prompt); }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { _login = null; RefreshScriptState(); ShowNotice(L.AutoLoginStoppedBecauseAPromptPatternTookToo); return; }
        if (step == LoginStep.None)
        {
            if (login.Sequence.Finished) { _login = null; RefreshScriptState(); }
            return;
        }
        login.Sending = true;
        var version = OutputVersion;
        // Both values bypass history and local echo, regardless of user preferences.
        _promptTerminal.Clear();
        Terminal.AppendLocalText("\n"); TerminalVersion++; Changed?.Invoke();
        try
        {
            await session.SendCommandAsync(step == LoginStep.Username ? login.Profile.Username : login.Password, _connectionCancellation?.Token ?? default);
            if (!ReferenceEquals(_session, session)) return;
            CommandsSent++;
        }
        catch
        {
            if (ReferenceEquals(_session, session)) { _login = null; ShowNotice(L.AutoLoginCouldNotSendCredentialsLogInManually); }
        }
        finally
        {
            login.Sending = false;
            if (ReferenceEquals(_login, login) && login.Sequence.Finished) _login = null;
            if (ReferenceEquals(_session, session))
            {
                FlushOutput(); RefreshScriptState(); Changed?.Invoke();
                // A response can arrive while the username write is completing.
                if (ReferenceEquals(_login, login) && version != OutputVersion) _ = TryAutoLoginAsync(session);
            }
        }
    }
}
