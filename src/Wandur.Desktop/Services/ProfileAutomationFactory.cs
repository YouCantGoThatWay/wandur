using System.Text;
using System.Text.Json;
using Wandur.Core.Scripting;
using Wandur.Core.Settings;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Services;

public interface IProfileAutomationFactory
{
    ProfileAutomationEditor Create(ConnectionProfile profile);
}

/// <summary>Offline edits stay in memory until the enclosing connection dialog saves.</summary>
public sealed class ProfileAutomationFactory(IScriptRuntimeFactory runtimes, IWorldScriptLibraryStore store) : IProfileAutomationFactory
{
    public ProfileAutomationEditor Create(ConnectionProfile profile)
    {
        var key = $"{profile.Host.Trim().ToLowerInvariant()}:{profile.Port}:{profile.UseTls}";
        var draft = new ProfileScriptDraftStore(store, key, string.IsNullOrWhiteSpace(profile.Host));
        var library = new WorldScriptLibrary(runtimes, draft, () => false, () => false, _ => Task.FromResult(false), _ => { });
        library.Configure(key, profile.Name);
        return new(library, draft);
    }
}

/// <summary>Buffers even add/delete/enable operations, which normally persist immediately.</summary>
internal sealed class ProfileScriptDraftStore : IWorldScriptLibraryStore
{
    private readonly IWorldScriptLibraryStore _store;
    private string _key;
    private List<WorldScriptDefinition> _baseline = [];
    private readonly List<WorldScriptDefinition> _draft = [];
    private bool _loaded;
    public ProfileScriptDraftStore(IWorldScriptLibraryStore store, string key, bool isNew)
    {
        _store = store; _key = key;
        _loaded = isNew;
    }
    public IReadOnlyList<WorldScriptDefinition> Load(string worldKey)
    {
        if (!_loaded)
        {
            _baseline = _store.Load(_key).ToList();
            _draft.AddRange(_baseline); _loaded = true;
        }
        return _draft.ToArray();
    }
    public void Upsert(string worldKey, WorldScriptDefinition definition)
    {
        var index = _draft.FindIndex(s => s.Id == definition.Id);
        if (index < 0) _draft.Add(definition); else _draft[index] = definition;
    }
    public void Delete(string worldKey, Guid id) => _draft.RemoveAll(s => s.Id == id);
    public bool HasChanges(IReadOnlyList<WorldScriptDefinition> definitions) => !_baseline.SequenceEqual(definitions);
    public void Commit(string key, IReadOnlyList<WorldScriptDefinition> definitions)
    {
        // Merge only this draft's changes, preserving unrelated edits made by another session.
        foreach (var previous in _baseline.Where(s => definitions.All(d => d.Id != s.Id)))
            _store.Delete(key, previous.Id);
        foreach (var definition in definitions.Where(s => key != _key || !_baseline.Contains(s)))
            _store.Upsert(key, definition);
        _key = key; _baseline = definitions.ToList();
    }
}

public sealed class ProfileAutomationEditor : IDisposable
{
    private readonly WorldScriptLibrary _library;
    private readonly ProfileScriptDraftStore? _draft;
    public ScriptLibraryViewModel Scripts { get; }
    public MacroLibraryViewModel Macros { get; }
    public bool HasUnsavedChanges => _draft?.HasChanges(Definitions(compile: false)) ?? _library.Items.Any(s => s.HasUnsavedChanges);
    public event Action? Changed;

    public ProfileAutomationEditor(WorldScriptLibrary library) : this(library, null) { }
    internal ProfileAutomationEditor(WorldScriptLibrary library, ProfileScriptDraftStore? draft)
    {
        _library = library; _draft = draft; Scripts = new(library); Macros = new(library);
        _library.Changed += OnChanged;
    }
    private void OnChanged() => Changed?.Invoke();
    private WorldScriptDefinition[] Definitions(bool compile) => _library.Items.Select(entry => entry.Saved with
    {
        Name = entry.Name, Source = compile && entry.Macro is { } macro ? MacroCompiler.Compile(macro) : entry.Source, Macro = entry.Macro
    }).ToArray();

    public void Validate()
    {
        var definitions = Definitions(compile: true);
        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 120 || definition.Name.Any(char.IsControl))
                throw new ArgumentException(L.ScriptInvalidName);
            if (Encoding.UTF8.GetByteCount(definition.Source) > WorldScriptStore.MaximumBytes) throw new ArgumentException(L.ScriptSourceTooLarge);
            MacroCompiler.Validate(definition);
        }
        if (definitions.Length > WorldScriptLibraryStore.MaximumScripts || JsonSerializer.SerializeToUtf8Bytes(definitions).Length > WorldScriptLibraryStore.MaximumBytes)
            throw new ArgumentException(L.ScriptLibraryTooLarge);
    }
    public async Task SaveAsync(string worldKey)
    {
        Validate();
        foreach (var entry in _library.Items.ToArray())
        {
            await _library.SaveAsync(entry);
            if (_library.Error is { } error) throw new IOException(error);
        }
        _draft?.Commit(worldKey, Definitions(compile: true));
        OnChanged();
    }
    public void Dispose()
    {
        _library.Changed -= OnChanged;
        Scripts.Dispose(); Macros.Dispose();
        _ = _library.DisposeAsync();
    }
}
