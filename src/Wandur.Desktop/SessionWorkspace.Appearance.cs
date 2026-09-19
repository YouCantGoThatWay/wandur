using Avalonia.Threading;
using Wandur.Core.Settings;

namespace Wandur.Desktop;

public sealed partial class SessionWorkspace
{
    private ClientSettings? _previewSettings;

    public void PreviewAppearanceSettings(ClientSettings settings)
    {
        settings.Validate();
        _previewSettings = settings;
        foreach (var tab in Tabs) tab.Controller.Display.ApplySettings(settings);
        ApplyAppearance();
    }
    public void EndAppearanceSettingsPreview()
    {
        _previewSettings = null;
        foreach (var tab in Tabs) tab.Controller.Display.ApplySettings(tab.Controller.Settings);
        ApplyAppearance();
    }

    /// <summary>
    /// A world theme belongs to an open session, not to a highlighted row: it applies when that session
    /// opens and whenever its tab is the active one, and the personal theme returns as soon as no active
    /// session carries one. Browsing the directory or the saved world list never changes the appearance.
    /// </summary>
    public void ApplyAppearance()
    {
        if (_disposed) return;
        SessionOpenTrace.Count("theme apply");
        using var trace = SessionOpenTrace.Measure("theme apply");
        var settings = _previewSettings ?? Active.Controller.Settings;
        var theme = settings.UseWorldThemes ? Active.Controller.WorldTheme : null;
        var oldImages = PrepareThemeImages(theme);
        ThemeService.Apply(settings, theme, _themeImages);
        if (oldImages is not null) foreach (var bitmap in oldImages.Values) bitmap.Dispose();
    }

    private void RefreshCatalogProfiles()
    {
        if (_disposed || _catalog is null || _catalog.Loading) return;
        ConnectionProfile Update(ConnectionProfile profile)
        {
            if (_catalog.FindEndpoint(profile.Host, profile.Port, profile.UseTls) is not { } listing) return profile;
            var mapping = listing.MappingForEndpoint(profile.Host, profile.Port, profile.UseTls) ?? profile.GetProtocolMapping();
            // Parsing another snapshot creates new arrays even when the mapping is unchanged.
            if (mapping is not null && profile.ProtocolMapping is { } previous &&
                System.Text.Json.JsonSerializer.Serialize(mapping, Wandur.Models.ModelJson.Options) ==
                System.Text.Json.JsonSerializer.Serialize(previous, Wandur.Models.ModelJson.Options)) mapping = previous;
            return profile with { Theme = listing.Theme, ProtocolMapping = mapping };
        }
        var settings = Active.Controller.Settings;
        var profiles = settings.Profiles.Select(Update).ToList();
        if (!profiles.SequenceEqual(settings.Profiles))
        {
            try { Active.Controller.SaveSettings(settings with { Profiles = profiles }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
            { Active.Controller.ShowNotice(Wandur.Core.Localization.Strings.WorldThemeCouldNotBeSaved); }
        }
        foreach (var tab in Tabs.Where(t => !t.IsClosing && t.Profile is not null))
        {
            tab.Profile = Update(tab.Profile!);
            tab.Controller.ApplyCatalogProfile(tab.Profile);
        }
        ApplyAppearance();
    }

    private void CatalogAppearanceChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshCatalogProfiles();
        else Dispatcher.UIThread.Post(RefreshCatalogProfiles);
    }
}
