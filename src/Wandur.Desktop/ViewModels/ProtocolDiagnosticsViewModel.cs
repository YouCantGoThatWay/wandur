using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wandur.Core.Protocol;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.ViewModels;

public sealed record ProtocolDiagnosticEntry(DateTimeOffset ReceivedAt, string Protocol, ProtocolDiagnosticContent? Content)
{
    public string Heading => $"{ReceivedAt.ToLocalTime():HH:mm:ss.fff}  {Protocol}  {Content?.Name}";
    /// <summary>The kind keys this message belongs to: the GMCP package, every MSDP variable it carries, or the telnet option name.</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];
}

/// <summary>One kind seen this session: a chip in the Messages tab with its count and whether it filters the list.</summary>
public sealed partial class ProtocolDiagnosticKind(string protocol, string name) : ObservableObject
{
    public string Protocol { get; } = protocol;
    public string Name { get; } = name;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Label))] private int _count;
    [ObservableProperty] private bool _isActive;
    public string Label => $"{Name} ({Count})";
}

/// <summary>Bounded, session-local protocol history. No payloads are written to disk.</summary>
public sealed partial class ProtocolDiagnosticsViewModel : ObservableObject
{
    public const int MaximumEntries = 200;
    public const int MaximumCharacters = 1048576;
    /// <summary>Chips shown before the "more" expander takes over; a large MSDP world reports about a hundred variables.</summary>
    public const int VisibleKindCap = 24;
    private int _characters;
    private bool _refilterSuspended;
    private readonly ProtocolSchemaInventory _schema = new();
    public string SchemaDetail => _schema.Snapshot;
    /// <summary>Every retained entry in wire order. Filtering never removes from here.</summary>
    public ObservableCollection<ProtocolDiagnosticEntry> Entries { get; } = [];
    /// <summary>The entries that pass the active chips and the filter text, in the same order; the list box shows this.</summary>
    public ObservableCollection<ProtocolDiagnosticEntry> Visible { get; } = [];
    /// <summary>All kinds seen since the last clear, sorted by protocol then name.</summary>
    public ObservableCollection<ProtocolDiagnosticKind> Kinds { get; } = [];
    /// <summary>The kinds the chip row renders: all of them when expanded, otherwise the first <see cref="VisibleKindCap"/>.</summary>
    public ObservableCollection<ProtocolDiagnosticKind> ChipKinds { get; } = [];
    [ObservableProperty] private ProtocolDiagnosticEntry? _selectedEntry;
    [ObservableProperty] private bool _follow = true;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _kindsExpanded;
    public bool IsEmpty => Entries.Count == 0;
    public bool HasActiveKinds => Kinds.Any(k => k.IsActive);
    public bool IsFiltering => HasActiveKinds || Filter.Length > 0;
    public int HiddenKindCount => Math.Max(0, Kinds.Count - VisibleKindCap);
    public bool HasMoreKinds => Kinds.Count > VisibleKindCap;
    public string MoreKindsLabel => L.Format(L.DiagnosticsMoreKinds, HiddenKindCount);
    public string CountLabel => IsFiltering ? L.Format(L.DiagnosticsShownOfTotal, Visible.Count, Entries.Count) : L.Format(L.DiagnosticsCount, Entries.Count, MaximumEntries);
    public string Detail => SelectedEntry is not { } entry ? L.DiagnosticsSelectMessage :
        entry.Content is not { } content ? L.DiagnosticsPrivate :
        string.Join("\n\n", new[] { content.Redacted ? L.DiagnosticsRedacted : null, content.Malformed && !content.Redacted ? L.DiagnosticsMalformed : null,
            content.Truncated ? L.DiagnosticsTruncated : null, content.Body }.Where(s => !string.IsNullOrEmpty(s)));

    public void Append(DateTimeOffset receivedAt, byte option, byte[]? payload)
        => AppendContent(receivedAt, option, payload is null ? null : ProtocolDiagnosticFormatter.Format(option, payload));

    public void AppendContent(DateTimeOffset receivedAt, byte option, ProtocolDiagnosticContent? content)
    {
        var protocol = ProtocolName(option);
        var entry = new ProtocolDiagnosticEntry(receivedAt, protocol, content) { Kinds = DeriveKinds(option, protocol, content) };
        var revision = _schema.Revision;
        if (entry.Content is { } observed) _schema.Observe(option, observed);
        if (revision != _schema.Revision) OnPropertyChanged(nameof(SchemaDetail));
        Entries.Add(entry); _characters += entry.Content?.Body.Length ?? 0;
        foreach (var kind in entry.Kinds) Count(protocol, kind);
        while (Entries.Count > MaximumEntries || _characters > MaximumCharacters)
        {
            _characters -= Entries[0].Content?.Body.Length ?? 0;
            var removed = Entries[0]; Entries.RemoveAt(0);
            if (Visible.Count > 0 && ReferenceEquals(Visible[0], removed)) Visible.RemoveAt(0);
            if (ReferenceEquals(SelectedEntry, removed)) SelectedEntry = null;
        }
        if (Matches(entry))
        {
            Visible.Add(entry);
            if (Follow) SelectedEntry = entry;
        }
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(CountLabel));
    }

    [RelayCommand] private void Clear()
    {
        Entries.Clear(); Visible.Clear(); _characters = 0; SelectedEntry = null;
        _schema.Clear(); OnPropertyChanged(nameof(SchemaDetail));
        foreach (var kind in Kinds) kind.PropertyChanged -= OnKindChanged;
        Kinds.Clear(); ChipKinds.Clear(); KindsExpanded = false;
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(HasActiveKinds)); OnPropertyChanged(nameof(IsFiltering));
        NotifyKindShape(); OnPropertyChanged(nameof(CountLabel));
    }

    /// <summary>Turns every chip off; the "All" chip.</summary>
    [RelayCommand] private void ClearKinds()
    {
        _refilterSuspended = true;
        try { foreach (var kind in Kinds) kind.IsActive = false; }
        finally { _refilterSuspended = false; }
        Refilter();
    }

    /// <summary>Clears the filter box; Escape in the view.</summary>
    [RelayCommand] private void ClearFilter() => Filter = "";

    /// <summary>Toggles the chip for a kind name; a no-op for a kind not seen yet.</summary>
    public void ToggleKind(string name)
    {
        if (Kinds.FirstOrDefault(k => k.Name == name) is { } kind) kind.IsActive = !kind.IsActive;
    }

    private void Count(string protocol, string name)
    {
        var index = 0;
        while (index < Kinds.Count)
        {
            var existing = Kinds[index];
            var order = string.CompareOrdinal(existing.Protocol, protocol);
            if (order == 0) order = string.Compare(existing.Name, name, StringComparison.OrdinalIgnoreCase);
            if (order == 0) { existing.Count++; return; }
            if (order > 0) break;
            index++;
        }
        var kind = new ProtocolDiagnosticKind(protocol, name) { Count = 1 };
        kind.PropertyChanged += OnKindChanged;
        Kinds.Insert(index, kind);
        RebuildChips(); NotifyKindShape();
    }

    private void OnKindChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ProtocolDiagnosticKind.IsActive) || _refilterSuspended) return;
        Refilter();
    }

    private void RebuildChips()
    {
        ChipKinds.Clear();
        foreach (var kind in KindsExpanded ? Kinds : Kinds.Take(VisibleKindCap)) ChipKinds.Add(kind);
    }

    private void NotifyKindShape()
    {
        OnPropertyChanged(nameof(HasMoreKinds)); OnPropertyChanged(nameof(HiddenKindCount)); OnPropertyChanged(nameof(MoreKindsLabel));
    }

    private bool Matches(ProtocolDiagnosticEntry entry)
    {
        if (HasActiveKinds && !entry.Kinds.Any(name => Kinds.Any(k => k.IsActive && k.Name == name))) return false;
        if (Filter.Length == 0) return true;
        if (entry.Kinds.Any(name => name.Contains(Filter, StringComparison.OrdinalIgnoreCase))) return true;
        return entry.Content is { } content && (content.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase) || content.Body.Contains(Filter, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One pass over the retained entries; the selection survives when it still shows, Follow moves to the last shown.</summary>
    private void Refilter()
    {
        var previous = SelectedEntry;
        var active = Kinds.Where(k => k.IsActive).Select(k => k.Name).ToHashSet(StringComparer.Ordinal);
        var text = Filter;
        Visible.Clear();
        foreach (var entry in Entries)
        {
            if (active.Count > 0 && !entry.Kinds.Any(active.Contains)) continue;
            if (text.Length > 0 && !entry.Kinds.Any(name => name.Contains(text, StringComparison.OrdinalIgnoreCase)) &&
                !(entry.Content is { } content && (content.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || content.Body.Contains(text, StringComparison.OrdinalIgnoreCase)))) continue;
            Visible.Add(entry);
        }
        SelectedEntry = Follow ? Visible.LastOrDefault() : previous is not null && Visible.Contains(previous) ? previous : null;
        OnPropertyChanged(nameof(HasActiveKinds)); OnPropertyChanged(nameof(IsFiltering)); OnPropertyChanged(nameof(CountLabel));
    }

    partial void OnFilterChanged(string value) => Refilter();
    partial void OnKindsExpandedChanged(bool value) => RebuildChips();
    partial void OnSelectedEntryChanged(ProtocolDiagnosticEntry? value) => OnPropertyChanged(nameof(Detail));
    partial void OnFollowChanged(bool value) { if (value) SelectedEntry = Visible.LastOrDefault(); }
    public void RefreshLanguage() { OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(CountLabel)); OnPropertyChanged(nameof(MoreKindsLabel)); }

    /// <summary>The label for the protocol column: GMCP, MSDP, or the telnet option name for anything else that reaches diagnostics.</summary>
    public static string ProtocolName(byte option) => option switch
    {
        201 => "GMCP", 69 => "MSDP", 70 => "MSSP", 31 => "NAWS", 24 => "TTYPE", 86 => "MCCP2", 87 => "MCCP3",
        91 => "MXP", 93 => "ZMP", 90 => "MSP", 200 => "ATCP", 42 => "CHARSET", 39 => "NEW-ENVIRON", 25 => "EOR",
        1 => "ECHO", 3 => "SGA", _ => $"OPTION {option}"
    };

    /// <summary>
    /// GMCP: the package name. MSDP: every variable name in the decoded body, read back from the formatter's JSON so a message
    /// with several variables belongs to each of them (the formatter's Name lists only the first three); a malformed or truncated
    /// body falls back to the names in the heading. Other options: the option name. A hidden payload has no kind.
    /// </summary>
    public static IReadOnlyList<string> DeriveKinds(byte option, string protocol, ProtocolDiagnosticContent? content)
    {
        if (content is null) return [];
        if (option == 201) return content.Name.Length == 0 ? [] : [content.Name];
        if (option != 69) return [protocol];
        if (content.Malformed || content.Body.Length == 0) return [content.Name];
        try
        {
            using var document = JsonDocument.Parse(content.Body, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var names = new List<string>();
                foreach (var property in document.RootElement.EnumerateObject())
                    if (property.Name.Length > 0 && !names.Contains(property.Name, StringComparer.Ordinal)) names.Add(property.Name);
                if (names.Count > 0) return names;
            }
        }
        catch (JsonException) { /* A truncated body: use the heading's names. */ }
        return content.Name.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
