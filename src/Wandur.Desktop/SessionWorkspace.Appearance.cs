using Avalonia.Threading;
using Wandur.Core.Discovery;
using Wandur.Core.Settings;

namespace Wandur.Desktop;

public sealed partial class SessionWorkspace
{
    // Most recently selected visible context wins. Leaving the directory page
    // returns to the selection underneath it; switching sessions clears previews.
    private sealed record AppearancePreview(object Owner, WorldTheme? Theme, ConnectionProfile? Profile);
    private readonly List<AppearancePreview> _appearancePreviews = [];
    private ClientSettings? _previewSettings;

    public void PreviewWorldTheme(object owner, WorldTheme? theme, bool activate = true)
    {
        if (_disposed) return;
        var index = _appearancePreviews.FindIndex(p => ReferenceEquals(p.Owner, owner));
        if (!activate)
        {
            if (index < 0) return;
            _appearancePreviews[index] = new(owner, theme, null);
        }
        else
        {
            if (index >= 0) _appearancePreviews.RemoveAt(index);
            _appearancePreviews.Add(new(owner, theme, null));
        }
        ApplyAppearance();
    }

    public void PreviewWorldProfile(object owner, ConnectionProfile? profile, bool activate = true)
    {
        if (_disposed) return;
        var index = _appearancePreviews.FindIndex(p => ReferenceEquals(p.Owner, owner));
        if (!activate)
        {
            if (index < 0) return;
            _appearancePreviews[index] = new(owner, null, profile);
        }
        else
        {
            if (index >= 0) _appearancePreviews.RemoveAt(index);
            _appearancePreviews.Add(new(owner, null, profile));
        }
        ApplyAppearance();
    }

    public void EndThemePreview(object owner)
    {
        if (_disposed) return;
        if (_appearancePreviews.RemoveAll(p => ReferenceEquals(p.Owner, owner)) > 0) ApplyAppearance();
    }

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

    public void ApplyAppearance()
    {
        if (_disposed) return;
        var theme = Active.Controller.WorldTheme;
        if (_appearancePreviews.LastOrDefault() is { } preview)
        {
            theme = preview.Theme;
            if (preview.Profile is { } selected)
            {
                var profile = Active.Controller.Settings.Profiles.FirstOrDefault(p => p.Id == selected.Id);
                if (profile is not null)
                    theme = _catalog?.FindEndpoint(profile.Host, profile.Port, profile.UseTls) is { } listing ? listing.Theme : profile.Theme;
            }
        }
        if (!(_previewSettings ?? Active.Controller.Settings).UseWorldThemes) theme = null;
        var oldImages = PrepareThemeImages(theme);
        ThemeService.Apply(_previewSettings ?? Active.Controller.Settings, theme, _themeImages);
        if (oldImages is not null) foreach (var bitmap in oldImages.Values) bitmap.Dispose();
    }

    private void ResetAppearanceSelection() { _appearancePreviews.Clear(); ApplyAppearance(); }
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
