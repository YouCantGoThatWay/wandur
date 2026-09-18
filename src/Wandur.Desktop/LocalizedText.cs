using System.Collections.Specialized;
using Avalonia.Data;
using Wandur.Core.Localization;

namespace Wandur.Desktop;

/// <summary>A live binding source for resource keys; never translates game or user text.</summary>
public sealed class LocalizedText : INotifyCollectionChanged
{
    public static LocalizedText Instance { get; } = new();
    // Avalonia indexer bindings observe collection resets.
    private LocalizedText() => UiLanguage.Changed += () =>
    {
        CollectionChanged?.Invoke(this, new(NotifyCollectionChangedAction.Reset));
    };
    public string this[string key] => Strings.ResourceManager.GetString(key, UiLanguage.Culture) ?? key;
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public static Binding Binding(string key) => new($"[{key}]") { Source = Instance, Mode = BindingMode.OneWay };
}
