using L = Wandur.Core.Localization.Strings;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Wandur.Desktop;

/// <summary>One set of actions shared by native menus, window menus, and shortcuts.</summary>
internal sealed class DesktopMenus
{
    private readonly MainWindow _window;
    private readonly List<MenuAction> _actions = [];
    private readonly List<Action> _refreshChecks = [];
    private Control? _editTarget;
    public NativeMenu Native { get; } = new();
    public Menu Fallback { get; } = new() { Name = "MainMenu" };
    public MenuAction Connect { get; }
    public MenuAction Disconnect { get; }
    private KeyModifiers Primary => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    public DesktopMenus(MainWindow window)
    {
        _window = window;
        window.GotFocus += (_, e) =>
        {
            if (e.Source is TextBox or SelectableTextBlock or Iciclecreek.Terminal.TerminalView) _editTarget = (Control)e.Source;
            Refresh();
        };
        var add = Action(nameof(L.AddWorld2), () => window.EditWorldAsync(), Key.N);
        var newTab = Action(nameof(L.FindAMUD), () => { window.Workspace.ShowSearch(); return Task.CompletedTask; }, Key.T);
        var demo = Action(nameof(L.OpenOfflineDemo), () => window.Sessions.OpenAsync());
        var save = Action(nameof(L.SaveTranscript), window.ExportAsync, Key.S, enabled: () => window.Controller.Display.PlainText.Length > 0);
        var close = Action(nameof(L.CloseWorkspaceItem), () => window.Workspace.CloseSelectedAsync(), Key.W);
        var preferences = Action(nameof(L.Preferences), window.PreferencesAsync, Key.OemComma);
        var about = Action(nameof(L.AboutWandur), () => window.ShowInformationAsync(L.AboutWandur, "Wandur", L.ADoorwayToOtherWorldsAnOpenSourceMUD));
        var quit = Action(OperatingSystem.IsMacOS() ? nameof(L.QuitWandur) : nameof(L.Exit), () => { window.Close(); return Task.CompletedTask; });
        var file = Group(nameof(L.File), newTab, add, Action(nameof(L.BrowseWorlds), window.BrowseWorldsAsync), demo, null, save, null, close);
        if (!OperatingSystem.IsMacOS()) AddItems(file.Native, file.Fallback, [null, quit]);

        var undo = Edit(nameof(L.Undo), Key.Z, t => t.Undo(), t => t.CanUndo);
        var redo = Edit(nameof(L.Redo), Key.Z, t => t.Redo(), t => t.CanRedo, KeyModifiers.Shift);
        var cut = Edit(nameof(L.Cut), Key.X, t => t.Cut(), t => t.CanCut && t.PasswordChar == '\0');
        var copy = Action(nameof(L.Copy), async () =>
        {
            if (EditTarget is TextBox t) t.Copy();
            else if (EditTarget is SelectableTextBlock block) block.Copy();
            else if (EditTarget is Iciclecreek.Terminal.TerminalView terminal) await terminal.CopyAsync();
        }, Key.C, enabled: () => EditTarget is TextBox { CanCopy: true, PasswordChar: '\0' } || EditTarget is SelectableTextBlock { SelectedText.Length: > 0 } || EditTarget is Iciclecreek.Terminal.TerminalView { Terminal.Selection.HasSelection: true }, bindShortcut: false);
        var paste = Edit(nameof(L.Paste), Key.V, t => t.Paste(), t => t.CanPaste);
        var selectAll = Action(nameof(L.SelectAll), () =>
        {
            if (EditTarget is TextBox t) t.SelectAll();
            else if (EditTarget is SelectableTextBlock block) block.SelectAll();
            else if (EditTarget is Iciclecreek.Terminal.TerminalView terminal) { terminal.Terminal.Selection.SelectAll(); terminal.InvalidateVisual(); }
            return Task.CompletedTask;
        }, Key.A, enabled: () => EditTarget is TextBox or SelectableTextBlock or Iciclecreek.Terminal.TerminalView, bindShortcut: false);
        var edit = Group(nameof(L.Edit), undo, redo, null, cut, copy, paste, selectAll);
        if (!OperatingSystem.IsMacOS()) AddItems(edit.Native, edit.Fallback, [null, preferences]);

        var view = Group(nameof(L.View),
            Action(nameof(L.Workspace), () => { window.TogglePanel(); return Task.CompletedTask; }),
            Action(nameof(L.MapPanel), () => { window.ToggleMap(); return Task.CompletedTask; }),
            Action(nameof(L.ChannelsPanel), () => { window.ToggleChannels(); return Task.CompletedTask; }),
            Action(nameof(L.RestorePanels), () => { window.ResetLayout(); return Task.CompletedTask; }), null,
            Action(nameof(L.ShowToolbar), () => { window.ToolbarVisible = !window.ToolbarVisible; return Task.CompletedTask; }),
            Action(nameof(L.FocusCommandInput), () => { window.FocusCommandInput(); return Task.CompletedTask; }, Key.L));
        Check(view, 0, () => window.IsPanelVisible());
        Check(view, 1, () => window.IsMapVisible);
        Check(view, 2, () => window.IsChannelsVisible);
        Check(view, 5, () => window.ToolbarVisible);

        Connect = Action(nameof(L.ConnectToSelectedWorld), window.ConnectSelectedAsync, Key.Return, enabled: () => window.SelectedProfile is not null);
        Disconnect = Action(nameof(L.Disconnect2), () => window.Controller.DisconnectAsync(), Key.D, enabled: () => window.Controller.IsConnected || window.Controller.IsConnecting);
        var session = Group(nameof(L.Session), Connect, Disconnect, null,
            Action(nameof(L.PrivateInput), () => { window.Controller.SetManualPrivate(!window.Controller.ManualPrivate); return Task.CompletedTask; }, enabled: () => window.Controller.IsConnected),
            Action(nameof(L.ClearTranscript), () => { window.Controller.ClearTranscript(); return Task.CompletedTask; }, enabled: () => window.Controller.Display.PlainText.Length > 0));
        AddItems(session.Native, session.Fallback, [null,
            Action(nameof(L.ScriptsMenu), window.ShowScriptsAsync, enabled: () => window.Sessions.Active.Profile is not null)]);
        Check(session, 3, () => window.Controller.ManualPrivate);
        Group(nameof(L.Window),
            Action(nameof(L.NextWorkspaceItem), () => { window.Workspace.SelectNextDocument(1); return Task.CompletedTask; }, Key.Tab, modifiers: KeyModifiers.Control, enabled: () => window.Workspace.Navigation.Entries.Count > 1),
            Action(nameof(L.PreviousWorkspaceItem), () => { window.Workspace.SelectNextDocument(-1); return Task.CompletedTask; }, Key.Tab, modifiers: KeyModifiers.Control | KeyModifiers.Shift, enabled: () => window.Workspace.Navigation.Entries.Count > 1), null,
            Action(nameof(L.Minimize), () => { window.WindowState = WindowState.Minimized; return Task.CompletedTask; }, OperatingSystem.IsMacOS() ? Key.M : null),
            Action(nameof(L.FullScreen), () => { window.WindowState = window.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen; return Task.CompletedTask; }, OperatingSystem.IsMacOS() ? Key.F : Key.F11, modifiers: OperatingSystem.IsMacOS() ? KeyModifiers.Meta | KeyModifiers.Control : KeyModifiers.None));
        Group(nameof(L.Help), Action(nameof(L.GettingStarted), () => window.ShowInformationAsync(L.WandurHelp, L.GettingStarted2, L.Format(L.GettingStartedHelp, OperatingSystem.IsMacOS() ? "⌘W" : "Ctrl+W"))), about);
        NativeMenu.SetMenu(window, Native);
        Native.NeedsUpdate += (_, _) => Refresh();
        Fallback.Opened += (_, _) => Refresh();
        Refresh();
    }

    private Control? EditTarget => _editTarget?.IsEffectivelyVisible == true && _editTarget.IsEffectivelyEnabled && TopLevel.GetTopLevel(_editTarget) == _window ? _editTarget : null;
    private MenuAction Edit(string label, Key key, Action<TextBox> execute, Func<TextBox, bool> enabled, KeyModifiers extra = KeyModifiers.None)
        => Action(label, () => { if (EditTarget is TextBox t) execute(t); return Task.CompletedTask; }, key, Primary | extra, () => EditTarget is TextBox t && enabled(t), false);

    private MenuAction Action(string label, Func<Task> execute, Key? key = null, KeyModifiers? modifiers = null, Func<bool>? enabled = null, bool bindShortcut = true)
    {
        var action = new MenuAction(label, execute, enabled ?? (() => true), ex => _window.Controller.ShowNotice(ex.Message));
        if (key is { } k)
        {
            action.Gesture = new KeyGesture(k, modifiers ?? Primary);
            // Text controls already own their edit shortcuts, including password protections.
            if (bindShortcut) _window.KeyBindings.Add(new KeyBinding { Gesture = action.Gesture, Command = action });
        }
        _actions.Add(action);
        return action;
    }

    private (NativeMenu Native, MenuItem Fallback) Group(string title, params MenuAction?[] actions)
    {
        var native = new NativeMenu();
        native.NeedsUpdate += (_, _) => Refresh();
        var fallback = new MenuItem { [!MenuItem.HeaderProperty] = LocalizedText.Binding(title) };
        Native.Items.Add(new NativeMenuItem { [!NativeMenuItem.HeaderProperty] = LocalizedText.Binding(title), Menu = native });
        Fallback.Items.Add(fallback);
        AddItems(native, fallback, actions);
        return (native, fallback);
    }
    private static NativeMenuItem NativeItem(MenuAction action) => new() { [!NativeMenuItem.HeaderProperty] = LocalizedText.Binding(action.LabelKey), Gesture = action.Gesture, Command = action };
    private static void AddItems(NativeMenu native, MenuItem fallback, MenuAction?[] actions)
    {
        foreach (var action in actions)
        {
            if (action is null) { native.Items.Add(new NativeMenuItemSeparator()); fallback.Items.Add(new Separator()); }
            else
            {
                native.Items.Add(NativeItem(action));
                fallback.Items.Add(new MenuItem { [!MenuItem.HeaderProperty] = LocalizedText.Binding(action.LabelKey), Command = action, InputGesture = action.Gesture });
            }
        }
    }
    private void Check((NativeMenu Native, MenuItem Fallback) menu, int index, Func<bool> value)
    {
        var native = (NativeMenuItem)menu.Native.Items[index];
        var fallback = (MenuItem)menu.Fallback.Items[index]!;
        native.ToggleType = MenuItemToggleType.CheckBox;
        _refreshChecks.Add(() => { native.IsChecked = value(); fallback.IsChecked = value(); });
    }
    public void Refresh()
    {
        foreach (var action in _actions) action.Refresh();
        foreach (var check in _refreshChecks) check();
    }
}

internal sealed class MenuAction(string label, Func<Task> execute, Func<bool> enabled, Action<Exception> error) : ICommand
{
    private bool _running;
    public string LabelKey { get; } = label;
    public string Label => LocalizedText.Instance[LabelKey];
    public KeyGesture? Gesture { get; set; }
    public bool CanExecute(object? parameter) => !_running && enabled();
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true; Refresh();
        try { await execute(); }
        catch (Exception ex) { error(ex); }
        finally { _running = false; Refresh(); }
    }
}
