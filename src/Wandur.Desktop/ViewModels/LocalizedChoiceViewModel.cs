using CommunityToolkit.Mvvm.ComponentModel;
using Wandur.Core.Localization;

namespace Wandur.Desktop.ViewModels;

/// <summary>A stable selection item whose label can change without resetting the selection.</summary>
public sealed class LocalizedChoiceViewModel(string resourceKey) : ObservableObject
{
    public string Label => Strings.ResourceManager.GetString(resourceKey, UiLanguage.Culture) ?? resourceKey;
    internal void Refresh() => OnPropertyChanged(nameof(Label));
}
