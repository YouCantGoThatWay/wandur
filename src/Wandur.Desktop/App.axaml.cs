using Wandur.Desktop.Services;
using Wandur.Core.Agents;
using System.Net.Http;
using Wandur.Core.Scripting;
using L = Wandur.Core.Localization.Strings;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wandur.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Wandur.Desktop.Security;

namespace Wandur.Desktop;

public partial class App : Application
{
    public static string? DataDirectory { get; set; }
    private ServiceProvider? _services;
    public override void Initialize()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime)
        {
            var directory = DataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wandur");
            _services = BuildServices(directory);
            Wandur.Core.Localization.UiLanguage.Apply(_services.GetRequiredService<ISettingsStore>().Load().Settings.Language);
        }
        AvaloniaXamlLoader.Load(this);
        ThemeService.Apply(new());
        WorkspaceFactory.RegisterTemplates(DataTemplates);
    }

    private async void AboutClicked(object? sender, EventArgs args)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
            await window.ShowInformationAsync(L.AboutWandur, "Wandur", L.ADoorwayToOtherWorldsAnOpenSourceMUD);
    }

    private async void PreferencesClicked(object? sender, EventArgs args)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
            await window.PreferencesAsync();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            var directory = DataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wandur");
            var provider = _services ??= BuildServices(directory);
            desktop.Exit += (_, _) => provider.Dispose();
            desktop.MainWindow = provider.GetRequiredService<MainWindow>();
        }
        base.OnFrameworkInitializationCompleted();
    }
    private static ServiceProvider BuildServices(string directory)
    {
        var services = new ServiceCollection();
        services.AddClientStorage(directory);
        services.AddSingleton<IPasswordVault>(_ => OperatingSystem.IsMacOS() ? new MacPasswordVault() :
            OperatingSystem.IsWindows() ? new WindowsPasswordVault() : new LinuxPasswordVault());
        services.AddSingleton<IScriptRuntimeFactory>(_ => new ProcessScriptRuntimeFactory(
            Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path"),
            string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)
                ? typeof(Program).Assembly.Location : null));
        services.AddSingleton<Wandur.Desktop.Terminal.ITranscriptDisplayFactory, Wandur.Desktop.Terminal.TranscriptDisplayFactory>();
        services.AddSingleton(_ => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan });
        services.AddSingleton<IAgentModelProvider, OpenAiCompatibleProvider>();
        services.AddSingleton<IAgentModelProvider, LmStudioNativeProvider>();
        services.AddSingleton<IAgentProviderResolver, AgentProviderRegistry>();
        services.AddSingleton<IAgentClientServices, AgentClientServices>();
        services.AddSingleton<IProfileAutomationFactory, ProfileAutomationFactory>();
        services.AddSingleton(provider => new Wandur.Core.Classification.RoomClassificationService(directory, provider.GetRequiredService<HttpClient>()));
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

}
