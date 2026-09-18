using System.Collections.ObjectModel;
using System.ComponentModel;
using Wandur.Core.Scripting;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Services;

public sealed class WorldScriptEntry : INotifyPropertyChanged
{
    private string _name;
    private string _source;
    private MacroDefinition? _macro;
    internal WorldScriptDefinition Saved { get; set; }
    internal WorldScriptEntry(WorldScriptDefinition definition, SessionScripts runtime)
    {
        Saved = definition; _name = definition.Name; _source = definition.Source; _macro = definition.Macro; Runtime = runtime;
        Runtime.Changed += () => PropertyChanged?.Invoke(this, new(nameof(Runtime)));
    }
    public Guid Id => Saved.Id;
    public string Name { get => _name; set { value ??= ""; if (_name == value) return; _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); } }
    public string Source { get => _source; set { value ??= ""; if (_source == value) return; _source = value; PropertyChanged?.Invoke(this, new(nameof(Source))); } }
    public MacroDefinition? Macro { get => _macro; set { if (_macro == value) return; _macro = value; PropertyChanged?.Invoke(this, new(nameof(Macro))); } }
    public bool IsMacro => Saved.Macro is not null;
    public bool Enabled => Saved.Enabled;
    public bool HasUnsavedChanges => Name != Saved.Name || Source != Saved.Source || Macro != Saved.Macro;
    public SessionScripts Runtime { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void NotifyEnabled() => PropertyChanged?.Invoke(this, new(nameof(Enabled)));
}

/// <summary>Owns independently enabled workers. Editor drafts are separate from saved, executable source.</summary>
public sealed class WorldScriptLibrary : IAsyncDisposable
{
    private readonly IScriptRuntimeFactory _factory;
    private readonly IWorldScriptLibraryStore _store;
    private readonly Func<bool> _canRun;
    private readonly Func<bool> _isPrivate;
    private readonly Func<string, Task<bool>> _send;
    private readonly Action<string> _echo;
    private readonly HashSet<Guid> _attempted = [];
    private readonly Queue<long> _sentAt = [];
    private string? _worldKey;
    private bool _disposed;
    private bool _suspended;
    private bool _refreshing;
    public ObservableCollection<WorldScriptEntry> Items { get; } = [];
    public bool MacrosEnabled { get; private set; } = true;
    public string WorldName { get; private set; } = "";
    public string? Error { get; private set; }
    public event Action? Changed;

    public WorldScriptLibrary(IScriptRuntimeFactory factory, IWorldScriptLibraryStore store, Func<bool> canRun,
        Func<bool> isPrivate, Func<string, Task<bool>> send, Action<string> echo)
    {
        _factory = factory; _store = store; _canRun = canRun; _isPrivate = isPrivate; _send = send; _echo = echo;
        Items.Add(Create(new(Guid.NewGuid(), L.ScriptDefaultName, ScriptExamples.Starter)));
    }

    private sealed class EntrySourceStore(string source) : IWorldScriptStore
    {
        public string? Load(string worldKey) => source;
        public void Save(string worldKey, string value) => source = value;
    }

    private WorldScriptEntry Create(WorldScriptDefinition definition)
    {
        var runtime = new SessionScripts(_factory, new EntrySourceStore(definition.Source), _canRun, _isPrivate, SendAsync, _echo);
        runtime.Configure(_worldKey ?? "", WorldName);
        runtime.Changed += OnChanged;
        var entry = new WorldScriptEntry(definition, runtime);
        entry.PropertyChanged += (_, args) => { if (args.PropertyName != nameof(WorldScriptEntry.Runtime)) OnChanged(); };
        return entry;
    }

    private Task<bool> SendAsync(string command)
    {
        var now = Environment.TickCount64;
        while (_sentAt.TryPeek(out var first) && now - first >= 60_000) _sentAt.Dequeue();
        if (_sentAt.Count >= 200 || _sentAt.Count(t => now - t < 1000) >= 20)
            throw new InvalidOperationException(L.ScriptRateExceeded);
        _sentAt.Enqueue(now);
        return _send(command);
    }

    private void OnChanged() { if (!_refreshing) Changed?.Invoke(); }

    public void Configure(string key, string name)
    {
        if (_disposed) return;
        Stop(); _attempted.Clear(); _sentAt.Clear(); WorldName = name;
        if (_worldKey != key)
        {
            _worldKey = key;
            foreach (var entry in Items) { entry.Runtime.Changed -= OnChanged; _ = entry.Runtime.DisposeAsync(); }
            Items.Clear(); Error = null;
            try { foreach (var definition in _store.Load(key)) Items.Add(Create(definition)); }
            catch (Exception ex) when (IsStorageError(ex)) { Error = L.Format(L.ScriptLoadFailed, ex.Message); }
        }
        Changed?.Invoke();
    }

    public WorldScriptEntry Add() => AddDefinition(new(Guid.NewGuid(), L.ScriptDefaultName, ScriptExamples.Starter));

    public WorldScriptEntry AddMacro()
    {
        var macro = new MacroDefinition(MacroKind.Trigger, "You are hungry", "eat bread");
        return AddDefinition(new(Guid.NewGuid(), L.MacroNewName, MacroCompiler.Compile(macro), Macro: macro));
    }

    private WorldScriptEntry AddDefinition(WorldScriptDefinition definition)
    {
        var entry = Create(definition);
        if (!_disposed && Items.Count < WorldScriptLibraryStore.MaximumScripts)
        {
            Items.Add(entry);
            Persist(entry.Saved);
        }
        else Error = L.ScriptLibraryTooLarge;
        Changed?.Invoke();
        return entry;
    }

    public async Task DeleteAsync(WorldScriptEntry entry)
    {
        if (_disposed || !Items.Contains(entry)) return;
        try
        {
            if (_worldKey is not null) _store.Delete(_worldKey, entry.Id);
            entry.Runtime.Changed -= OnChanged;
            await entry.Runtime.DisposeAsync();
            Items.Remove(entry); _attempted.Remove(entry.Id); Error = null;
        }
        catch (Exception ex) when (IsStorageError(ex)) { Error = L.Format(L.ScriptSaveFailed, ex.Message); }
        Changed?.Invoke();
    }

    public async Task SetEnabledAsync(WorldScriptEntry entry, bool enabled, bool persist = true)
    {
        if (_disposed || !Items.Contains(entry) || entry.Enabled == enabled) return;
        var saved = entry.Saved with { Enabled = enabled };
        if (persist && !Persist(saved)) return;
        entry.Saved = saved; entry.NotifyEnabled();
        entry.Runtime.Stop(); _attempted.Remove(entry.Id);
        await ActivateAsync(entry);
        Changed?.Invoke();
    }

    public async Task SaveAsync(WorldScriptEntry entry)
    {
        if (_disposed || !Items.Contains(entry)) return;
        WorldScriptDefinition saved;
        try { saved = entry.Saved with { Name = entry.Name.Trim(), Source = entry.Macro is { } macro ? MacroCompiler.Compile(macro) : entry.Source, Macro = entry.Macro }; }
        catch (ArgumentException error) { Error = error.Message; Changed?.Invoke(); return; }
        if (!Persist(saved)) return;
        entry.Saved = saved; entry.Name = saved.Name; entry.Source = saved.Source;
        entry.Runtime.Source = saved.Source;
        entry.Runtime.Stop(); _attempted.Remove(entry.Id);
        await ActivateAsync(entry);
        Changed?.Invoke();
    }

    private static bool IsStorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException;
    private bool Persist(WorldScriptDefinition definition)
    {
        try
        {
            if (_worldKey is null) throw new IOException(L.ScriptNeedConnection);
            _store.Upsert(_worldKey, definition); Error = null;
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex))
        { Error = L.Format(L.ScriptSaveFailed, ex.Message); Changed?.Invoke(); return false; }
    }

    private Task ActivateAsync(WorldScriptEntry entry)
    {
        if (_disposed || _suspended || (entry.IsMacro && !MacrosEnabled) || !entry.Enabled || !Items.Contains(entry) || !_canRun() || _isPrivate() || !_attempted.Add(entry.Id)) return Task.CompletedTask;
        entry.Runtime.Source = entry.Saved.Source;
        return entry.Runtime.RunAsync();
    }

    public async Task SetMacrosEnabledAsync(bool enabled)
    {
        if (_disposed || MacrosEnabled == enabled) return;
        MacrosEnabled = enabled;
        foreach (var entry in Items.Where(e => e.IsMacro).ToArray())
        {
            entry.Runtime.Stop(); _attempted.Remove(entry.Id);
            if (enabled) await ActivateAsync(entry);
        }
        Changed?.Invoke();
    }

    public async Task ReloadAsync()
    {
        if (_disposed || _worldKey is null) return;
        IReadOnlyList<WorldScriptDefinition> definitions;
        try { definitions = _store.Load(_worldKey); }
        catch (Exception error) when (IsStorageError(error))
        { Error = L.Format(L.ScriptLoadFailed, error.Message); Changed?.Invoke(); return; }
        _refreshing = true;
        try
        {
            foreach (var entry in Items) { entry.Runtime.Changed -= OnChanged; await entry.Runtime.DisposeAsync(); }
            Items.Clear(); _attempted.Clear(); Error = null;
            foreach (var definition in definitions) Items.Add(Create(definition));
        }
        finally { _refreshing = false; }
        RefreshState();
    }

    public void RefreshState()
    {
        if (_disposed) return;
        _refreshing = true;
        try
        {
            foreach (var entry in Items)
            {
                entry.Runtime.RefreshState();
                _ = ActivateAsync(entry);
            }
        }
        finally { _refreshing = false; }
        Changed?.Invoke();
    }
    public void Tick() { foreach (var entry in Items) entry.Runtime.Tick(); }
    public void Feed(string text) { foreach (var entry in Items) entry.Runtime.Feed(text); }
    public void Publish(ScriptEvent input) { foreach (var entry in Items) entry.Runtime.Publish(input); }
    public bool HandleShortcut(string key)
    {
        if (_disposed || _suspended || !_canRun() || _isPrivate()) return false;
        var matches = Items.Where(entry => entry.Enabled && entry.Runtime.IsRunning && entry.Saved.Macro is { Kind: MacroKind.Shortcut } macro && macro.Pattern == key).ToArray();
        foreach (var entry in matches) entry.Runtime.Publish(new("key", key));
        return matches.Length > 0;
    }
    public void DiscardPartialLine() { foreach (var entry in Items) entry.Runtime.DiscardPartialLine(); }
    public async Task<bool> HandleCommandAsync(string command)
    {
        foreach (var entry in Items.ToArray())
            if (await entry.Runtime.HandleCommandAsync(command)) return true;
        return false;
    }
    public void SetSuspended(bool suspended)
    {
        if (_suspended == suspended) return;
        _suspended = suspended;
        if (suspended) Stop();
        else
        {
            // Re-evaluating a script can replay top-level sends. Resume only on an explicit
            // reload/toggle, rather than sending extra commands after an agent step or Stop.
            foreach (var entry in Items.Where(entry => entry.Enabled)) _attempted.Add(entry.Id);
            Changed?.Invoke();
        }
    }

    public void Stop() { foreach (var entry in Items) entry.Runtime.Stop(); }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in Items) { entry.Runtime.Changed -= OnChanged; await entry.Runtime.DisposeAsync(); }
    }
}
