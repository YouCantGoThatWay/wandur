using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wandur.Core.Protocol;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.ViewModels;

public sealed record ProtocolDiagnosticEntry(DateTimeOffset ReceivedAt, string Protocol, ProtocolDiagnosticContent? Content)
{
    public string Heading => $"{ReceivedAt.ToLocalTime():HH:mm:ss.fff}  {Protocol}  {Content?.Name}";
}

/// <summary>Bounded, session-local protocol history. No payloads are written to disk.</summary>
public sealed partial class ProtocolDiagnosticsViewModel : ObservableObject
{
    public const int MaximumEntries = 200;
    public const int MaximumCharacters = 1048576;
    private int _characters;
    private readonly ProtocolSchemaInventory _schema = new();
    public string SchemaDetail => _schema.Snapshot;
    public ObservableCollection<ProtocolDiagnosticEntry> Entries { get; } = [];
    [ObservableProperty] private ProtocolDiagnosticEntry? _selectedEntry;
    [ObservableProperty] private bool _follow = true;
    public bool IsEmpty => Entries.Count == 0;
    public string CountLabel => L.Format(L.DiagnosticsCount, Entries.Count, MaximumEntries);
    public string Detail => SelectedEntry is not { } entry ? L.DiagnosticsSelectMessage :
        entry.Content is not { } content ? L.DiagnosticsPrivate :
        string.Join("\n\n", new[] { content.Redacted ? L.DiagnosticsRedacted : null, content.Malformed && !content.Redacted ? L.DiagnosticsMalformed : null,
            content.Truncated ? L.DiagnosticsTruncated : null, content.Body }.Where(s => !string.IsNullOrEmpty(s)));

    public void Append(DateTimeOffset receivedAt, byte option, byte[]? payload)
        => AppendContent(receivedAt, option, payload is null ? null : ProtocolDiagnosticFormatter.Format(option, payload));

    public void AppendContent(DateTimeOffset receivedAt, byte option, ProtocolDiagnosticContent? content)
    {
        var entry = new ProtocolDiagnosticEntry(receivedAt, option == 201 ? "GMCP" : "MSDP", content);
        var revision = _schema.Revision;
        if (entry.Content is { } observed) _schema.Observe(option, observed);
        if (revision != _schema.Revision) OnPropertyChanged(nameof(SchemaDetail));
        Entries.Add(entry); _characters += entry.Content?.Body.Length ?? 0;
        while (Entries.Count > MaximumEntries || _characters > MaximumCharacters)
        {
            _characters -= Entries[0].Content?.Body.Length ?? 0;
            var removed = Entries[0]; Entries.RemoveAt(0);
            if (ReferenceEquals(SelectedEntry, removed)) SelectedEntry = null;
        }
        if (Follow) SelectedEntry = entry;
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(CountLabel));
    }
    [RelayCommand] private void Clear()
    {
        Entries.Clear(); _characters = 0; SelectedEntry = null;
        _schema.Clear(); OnPropertyChanged(nameof(SchemaDetail));
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(CountLabel));
    }
    partial void OnSelectedEntryChanged(ProtocolDiagnosticEntry? value) => OnPropertyChanged(nameof(Detail));
    partial void OnFollowChanged(bool value) { if (value) SelectedEntry = Entries.LastOrDefault(); }
    public void RefreshLanguage() { OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(CountLabel)); }
}
