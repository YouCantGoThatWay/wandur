using System.Collections.ObjectModel;
using Wandur.Core.Scripting;
using Wandur.Core.Terminal;

namespace Wandur.Desktop.Services;

/// <summary>One widget a script declared. Property updates keep the same instance so the view
/// changes the rendered control in place instead of rebuilding the panel.</summary>
public sealed class ScriptPanelWidget(string id, string kind, ScriptWidgetProperties properties)
{
    public string Id { get; } = id;
    public string Kind { get; private set; } = kind;
    public ScriptWidgetProperties Properties { get; private set; } = properties;
    /// <summary>Raised with true when the widget kind changed and its control must be rebuilt.</summary>
    public event Action<bool>? Changed;

    internal void Update(string kind, ScriptWidgetProperties properties)
    {
        var rebuilt = Kind != kind;
        Kind = kind; Properties = properties;
        Changed?.Invoke(rebuilt);
    }
}

/// <summary>A panel one script declared, rendered by the client in the session rail (or the vitals strip for bars).</summary>
public sealed class ScriptPanel(Guid scriptId, string id, string title, string dock)
{
    private readonly List<ScriptPanelWidget> _widgets = [];
    public Guid ScriptId { get; } = scriptId;
    public string Id { get; } = id;
    public string Title { get; private set; } = title;
    public string Dock { get; private set; } = dock;
    public bool IsVisible { get; private set; } = true;
    /// <summary>True while a focus request waits for the workspace to bring the panel's tab to the front.</summary>
    public bool FocusRequested { get; private set; }
    private long? _focusedAt;
    public IReadOnlyList<ScriptPanelWidget> Widgets => _widgets;
    /// <summary>The panel lives in the vitals strip under the transcript rather than a docked tool.</summary>
    public bool IsBars => Dock == ScriptPanelAction.DockBars;
    /// <summary>Raised when the title, dock side, visibility, focus request or the set of widgets changed.</summary>
    public event Action? Changed;
    internal event Action<ScriptPanel, string>? Invoked;

    /// <summary>Sends one widget callback back to the script that declared this panel.</summary>
    public void Invoke(string widget, string name, bool? flag = null, string? text = null)
    {
        if (!ScriptPanelAction.EventNames.Contains(name, StringComparer.Ordinal)) return;
        if (!_widgets.Any(candidate => candidate.Id == widget)) return;
        Invoked?.Invoke(this, ScriptPanelAction.EventJson(Id, widget, name, flag, text));
    }

    internal bool Describe(string title, string dock)
    {
        if (Title == title && Dock == dock) return false;
        Title = title; Dock = dock;
        Changed?.Invoke();
        return true;
    }

    internal void Apply(ScriptPanelAction action)
    {
        switch (action.Action)
        {
            case "widget":
                var widget = _widgets.FirstOrDefault(candidate => candidate.Id == action.Widget);
                if (widget is null)
                {
                    if (_widgets.Count >= ScriptPanelAction.MaximumWidgets) return;
                    _widgets.Add(new(action.Widget, action.Kind, action.Properties));
                    Changed?.Invoke();
                }
                else widget.Update(action.Kind, action.Properties);
                break;
            case "remove":
                if (_widgets.RemoveAll(candidate => candidate.Id == action.Widget) > 0) Changed?.Invoke();
                break;
            case "show" or "hide":
                var visible = action.Action == "show";
                if (IsVisible == visible) return;
                IsVisible = visible;
                Changed?.Invoke();
                break;
            case "focus":
                // Nothing to bring to the front in the strip. Elsewhere one request per second is kept, so a
                // script refreshing on every MSDP event cannot hold the user's tab hostage.
                if (IsBars) return;
                var now = Environment.TickCount64;
                if (_focusedAt is { } last && now - last < ScriptPanelAction.FocusInterval.TotalMilliseconds) return;
                _focusedAt = now;
                IsVisible = true;
                FocusRequested = true;
                Changed?.Invoke();
                break;
        }
    }

    /// <summary>Consumes the pending focus request, if any. The workspace calls it once it has activated the tab.</summary>
    public bool TakeFocusRequest()
    {
        if (!FocusRequested) return false;
        FocusRequested = false;
        return true;
    }
}

/// <summary>The panels a session's scripts currently declare. Panels belong to one session and
/// disappear with the script that created them.</summary>
public sealed class ScriptPanelHost
{
    public ObservableCollection<ScriptPanel> Panels { get; } = [];
    /// <summary>Raised when a panel was added, removed or changed. The session rail refreshes from this.</summary>
    public event Action? Changed;
    /// <summary>Delivers one widget callback to the script that owns the panel.</summary>
    internal Action<Guid, string>? Callback { get; set; }

    internal void Apply(Guid scriptId, ScriptPanelAction action)
    {
        var panel = Panels.FirstOrDefault(candidate => candidate.ScriptId == scriptId && candidate.Id == action.Panel);
        if (action.Action == "close")
        {
            if (panel is not null) { Panels.Remove(panel); Changed?.Invoke(); }
            return;
        }
        if (panel is null)
        {
            // Only a declaration creates a panel; a stray update for an unknown panel is ignored.
            if (action.Action != "create") return;
            if (Panels.Count(candidate => candidate.ScriptId == scriptId) >= ScriptPanelAction.MaximumPanels) return;
            panel = new(scriptId, action.Panel, Title(action), action.Dock);
            panel.Invoked += (owner, message) => Callback?.Invoke(owner.ScriptId, message);
            panel.Changed += () => Changed?.Invoke();
            Panels.Add(panel);
            Changed?.Invoke();
            return;
        }
        if (action.Action == "create") panel.Describe(Title(action), action.Dock);
        else panel.Apply(action);
    }

    // Rail tabs are plain text, so a title such as "&228A Vicious Womprat&D" loses its codes here.
    private static string Title(ScriptPanelAction action) => action.Title.Length > 0 ? MudColorCodes.Strip(action.Title) : action.Panel;

    /// <summary>Retires one panel, for example because the user closed its rail tab.</summary>
    public void Close(ScriptPanel panel)
    {
        if (!Panels.Remove(panel)) return;
        Changed?.Invoke();
    }

    internal void RemoveScript(Guid scriptId)
    {
        var removed = Panels.Where(candidate => candidate.ScriptId == scriptId).ToArray();
        if (removed.Length == 0) return;
        foreach (var panel in removed) Panels.Remove(panel);
        Changed?.Invoke();
    }

    internal void Clear()
    {
        if (Panels.Count == 0) return;
        Panels.Clear();
        Changed?.Invoke();
    }
}
