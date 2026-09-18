using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Settings;
using Wandur.Desktop;
using Wandur.Desktop.Security;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class LoginTests
{
    private static SettingsStore Store() => new(Path.Combine(Path.GetTempPath(), "wandur-login-" + Guid.NewGuid(), "settings.json"));
    private static async Task WaitFor(Func<bool> predicate)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
        Assert.True(predicate());
    }

    [AvaloniaTheory]
    [InlineData("Pass", "word: ")]
    [InlineData("(P)", "assword: ")]
    public async Task FragmentedColoredPromptsSendCredentialsOnceWithoutEchoOrHistory(string first, string second)
    {
        var vault = new MemoryPasswordVault();
        var store = Store();
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, vault, new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var profile = new ConnectionProfile { Name = "Test", Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Username = "test-user", AutoLogin = true };
        await controller.SaveWorldAsync(profile, "p@ss-test-only", true);
        profile = Assert.Single(controller.Settings.Profiles);
        controller.SaveSettings(controller.Settings with { LocalEcho = true });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await controller.StartAsync(profile);
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        using var reader = new StreamReader(server.GetStream(), Encoding.UTF8, leaveOpen: true);
        async Task Send(string text) => await server.GetStream().WriteAsync(Encoding.UTF8.GetBytes(text), timeout.Token);
        await Send("\u001b[1;36mUserna");
        await WaitFor(() => controller.Terminal.PlainText.Contains("Userna"));
        Assert.Equal(0, server.Available);
        await Send("me: \u001b[0m");
        Assert.Equal("test-user", await reader.ReadLineAsync(timeout.Token));
        await Send("\r\n\u001b[0m ");
        await Task.Delay(100); Dispatcher.UIThread.RunJobs();
        await Send("\u001b[1;30m" + first);
        await WaitFor(() => controller.Terminal.PlainText.Contains(first));
        Assert.Equal(0, server.Available);
        await Send(second + "\u001b[0m");
        Assert.Equal("p@ss-test-only", await reader.ReadLineAsync(timeout.Token));
        await Send("Wrong password.\r\nPassword: ");
        await WaitFor(() => controller.Terminal.PlainText.Contains("Wrong password."));
        await Task.Delay(120); Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, server.Available);
        Assert.Equal("draft", controller.History.Previous("draft"));
        Assert.DoesNotContain("p@ss-test-only", controller.Terminal.PlainText);
        Assert.DoesNotContain("test-user", controller.Terminal.PlainText);
        Assert.DoesNotContain("p@ss-test-only", File.ReadAllText(store.FilePath));
        await controller.DisconnectAsync();
        Directory.Delete(Path.GetDirectoryName(store.FilePath)!, true);
    }

    [AvaloniaFact]
    public async Task ManualInputCancelsAutoLoginAndMissingPasswordsAllowManualConnection()
    {
        var vault = new MemoryPasswordVault();
        var store = Store();
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, vault, new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var profile = new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Username = "saved-name", AutoLogin = true };
        await controller.SaveWorldAsync(profile, "saved-secret", true);
        profile = Assert.Single(controller.Settings.Profiles);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await controller.StartAsync(profile);
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        using var reader = new StreamReader(server.GetStream(), Encoding.UTF8, leaveOpen: true);
        await controller.SendAsync("manual-name");
        Assert.Equal("manual-name", await reader.ReadLineAsync(timeout.Token));
        await server.GetStream().WriteAsync(Encoding.UTF8.GetBytes("Name: "), timeout.Token);
        await WaitFor(() => controller.Terminal.PlainText.Contains("Name: "));
        Assert.Equal(0, server.Available);
        await controller.DisconnectAsync();
        await vault.DeleteAsync(PasswordVault.Key(profile));
        await controller.StartAsync(profile);
        using var other = await listener.AcceptTcpClientAsync(timeout.Token);
        Assert.True(controller.IsConnected);
        Assert.Contains("wasn't found", controller.Notice);
        await controller.DisconnectAsync();
        Directory.Delete(Path.GetDirectoryName(store.FilePath)!, true);
    }

    [AvaloniaTheory]
    [InlineData("Password:\r\n", null)]
    [InlineData("Secret?\r\n", "^Secret\\?$")]
    public async Task ManualPasswordsStayPrivateForNewlineAndCustomPrompts(string prompt, string? pattern)
    {
        var store = Store();
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        controller.SaveSettings(controller.Settings with { LocalEcho = true });
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var profile = new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        if (pattern is not null) profile = profile with { PasswordPrompt = pattern };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await controller.StartAsync(profile);
        using var server = await listener.AcceptTcpClientAsync(timeout.Token);
        await server.GetStream().WriteAsync(Encoding.UTF8.GetBytes(prompt), timeout.Token);
        await WaitFor(() => controller.Terminal.PlainText.Contains(prompt.Trim()));
        Assert.True(controller.IsPrivate);
        await controller.SendAsync("manual-secret");
        Assert.DoesNotContain("manual-secret", controller.Terminal.PlainText);
        Assert.Equal("draft", controller.History.Previous("draft"));
        byte[] gmcp = [255, 251, 201, 255, 250, 201, .. Encoding.UTF8.GetBytes("Auth.Info {\"message\":\"Rejected manual-secret\"}"), 255, 240];
        await server.GetStream().WriteAsync(gmcp, timeout.Token);
        await WaitFor(() => controller.Diagnostics.Entries.Count > 0);
        Assert.Contains("Rejected [redacted]", controller.Diagnostics.Entries[^1].Content!.Body);
        await controller.DisconnectAsync();
        Directory.Delete(Path.GetDirectoryName(store.FilePath)!, true);
    }

    [AvaloniaFact]
    public async Task ProfileEditKeepsPasswordOnlyForSameEndpointAndAccount()
    {
        var vault = new MemoryPasswordVault(); var store = Store();
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, vault, new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var profile = new ConnectionProfile { Host = "first.example.org", Username = "player", AutoLogin = true };
        await controller.SaveWorldAsync(profile, "secret-one", true);
        profile = Assert.Single(controller.Settings.Profiles);
        var oldKey = PasswordVault.Key(profile);
        await controller.SaveWorldAsync(profile with { Name = "Renamed" }, "", true);
        Assert.Equal("secret-one", await vault.ReadAsync(oldKey));
        await Assert.ThrowsAsync<ArgumentException>(() => controller.SaveWorldAsync(profile with { Host = "other.example.org" }, "", true));
        Assert.Equal("first.example.org", Assert.Single(controller.Settings.Profiles).Host);
        await controller.SaveWorldAsync(profile with { Host = "other.example.org" }, "secret-two", true);
        var updated = Assert.Single(controller.Settings.Profiles);
        Assert.Null(await vault.ReadAsync(oldKey));
        Assert.Equal("secret-two", await vault.ReadAsync(PasswordVault.Key(updated)));
        await controller.SaveWorldAsync(updated, "", false);
        Assert.Null(await vault.ReadAsync(PasswordVault.Key(updated)));
        Assert.Null(Assert.Single(controller.Settings.Profiles).PasswordId);
        Assert.False(Assert.Single(controller.Settings.Profiles).AutoLogin);
        Assert.DoesNotContain("secret-", File.ReadAllText(store.FilePath));
        Directory.Delete(Path.GetDirectoryName(store.FilePath)!, true);
    }

    [AvaloniaFact]
    public async Task FailedSettingsSaveRemovesNewCredentialAndKeepsExistingCredential()
    {
        var vault = new MemoryPasswordVault();
        var store = Store();
        await using var controller = new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, vault, new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var profile = new ConnectionProfile { Host = "example.org", Username = "player", AutoLogin = true };
        await controller.SaveWorldAsync(profile, "original-secret", true);
        profile = Assert.Single(controller.Settings.Profiles);
        var originalKey = PasswordVault.Key(profile);
        // A directory at the destination deterministically prevents the atomic settings-file move.
        File.Delete(store.FilePath);
        Directory.CreateDirectory(store.FilePath);
        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() => controller.SaveWorldAsync(profile, "replacement-secret", true));
            Assert.Equal(1, vault.Count);
            Assert.Equal("original-secret", await vault.ReadAsync(originalKey));
            Assert.Equal(profile, Assert.Single(controller.Settings.Profiles));
        }
        finally
        {
            Directory.Delete(store.FilePath);
            await controller.RemoveWorldAsync(profile);
            Assert.Empty(controller.Settings.Profiles);
            Assert.Equal(0, vault.Count);
            Directory.Delete(Path.GetDirectoryName(store.FilePath)!, true);
        }
    }

    [AvaloniaFact]
    public async Task LoginFieldsSaveThroughDialogAndRightClickTargetsClickedRow()
    {
        var vault = new MemoryPasswordVault(); var store = Store();
        var window = new MainWindow(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(), store, vault, new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore()); window.Show();
        try
        {
            var first = new ConnectionProfile { Name = "First", Host = "first.example.org" };
            var second = new ConnectionProfile { Name = "Second", Host = "second.example.org" };
            window.Controller.SaveSettings(window.Controller.Settings with { Profiles = [first, second] });
            Dispatcher.UIThread.RunJobs();
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(c => c.Name == "WorldProfiles");
            list.SelectedItem = first;
            var row = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(1));
            var menu = row.ContextMenu!.Items.OfType<MenuItem>().Single(m => m.Name == "EditWorldMenu");
            menu.Command!.Execute(menu.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.OwnedWindows.OfType<ProfileDialog>());
            T Find<T>(string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
            Assert.Equal("Second", Find<TextBox>("WorldName").Text);
            Find<ListBox>("ProfileSections").SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Find<TextBox>("LoginUsername").Text = "test-player";
            Find<CheckBox>("RememberPassword").IsChecked = true;
            Find<TextBox>("LoginPassword").Text = "dialog-secret";
            Find<CheckBox>("AutoLogin").IsChecked = true;
            Find<Button>("SaveWorld").Command!.Execute(null);
            await WaitFor(() => !dialog.IsVisible);
            var saved = window.Controller.Settings.Profiles.Single(p => p.Id == second.Id);
            Assert.Equal("test-player", saved.Username);
            Assert.True(saved.AutoLogin);
            Assert.Equal("dialog-secret", await vault.ReadAsync(PasswordVault.Key(saved)));
            Assert.DoesNotContain("dialog-secret", File.ReadAllText(store.FilePath));
        }
        finally { await window.Sessions.DisposeAsync(); window.Close(); Directory.Delete(Path.GetDirectoryName(store.FilePath)!, true); }
    }

    [Fact]
    public async Task NativeVaultRoundTripWhenExplicitlyEnabled()
    {
        // Opt-in: exercises the real OS store with a throwaway credential, never a user's saved login.
        if (Environment.GetEnvironmentVariable("WANDUR_TEST_NATIVE_VAULT") != "1") return;
        IPasswordVault vault = OperatingSystem.IsMacOS() ? new MacPasswordVault() : OperatingSystem.IsWindows() ? new WindowsPasswordVault() : new LinuxPasswordVault();
        var key = "integration-" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Null(await vault.ReadAsync(key));
            await vault.WriteAsync(key, "test-only-π-秘密");
            Assert.Equal("test-only-π-秘密", await vault.ReadAsync(key));
            await vault.WriteAsync(key, "replacement-test");
            Assert.Equal("replacement-test", await vault.ReadAsync(key));
            await vault.DeleteAsync(key);
            Assert.Null(await vault.ReadAsync(key));
        }
        finally { await vault.DeleteAsync(key); }
    }
}
