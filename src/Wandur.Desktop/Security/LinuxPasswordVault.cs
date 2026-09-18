using L = Wandur.Core.Localization.Strings;
using System.Diagnostics;

namespace Wandur.Desktop.Security;

/// <summary>Uses libsecret's CLI with the password on stdin, never in command-line arguments.</summary>
public sealed class LinuxPasswordVault : IPasswordVault
{
    public string Description => L.LinuxVaultName;
    public async Task<string?> ReadAsync(string key) => await RunAsync("lookup", key);
    public async Task WriteAsync(string key, string password)
    {
        PasswordVault.ValidatePassword(password);
        await RunAsync("store", key, password);
    }
    public async Task DeleteAsync(string key) => _ = await RunAsync("clear", key);

    private static async Task<string?> RunAsync(string action, string key, string? password = null)
    {
        var info = new ProcessStartInfo("secret-tool") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add(action);
        if (action == "store") info.ArgumentList.Add("--label=Wandur world login");
        info.ArgumentList.Add("application"); info.ArgumentList.Add("net.wandur.client");
        info.ArgumentList.Add("credential"); info.ArgumentList.Add(key);
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException(L.InstallSecretToolLibsecretToolsOnDebianUbuntuLibsecret); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            if (password is not null) await process.StandardInput.WriteAsync(password.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var result = await output;
            var errorText = await error;
            if (process.ExitCode == 0) return result; // Redirected lookup output has no added newline.
            if (process.ExitCode == 1 && errorText.Length == 0 && action is "lookup" or "clear") return null;
            throw new InvalidOperationException(L.SecretServiceCouldNotCompleteTheRequestUnlockYour);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new InvalidOperationException(L.TheKeyringRequestTimedOutUnlockYourDesktopKeyring);
        }
    }
}
