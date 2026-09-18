using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;
using Wandur.Core.Settings;
using Wandur.Desktop.Services;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

public sealed partial class ProfileDialog : Window
{
    public ProfileDialog() => AvaloniaXamlLoader.Load(this);

    public ProfileDialog(ProfileEditorViewModel model) : this()
    {
        DataContext = model;
        Ui.ToolbarIconKey(this.FindControl<Button>("ToggleProfileSections")!, "M 2,2 H 14 V 14 H 2 Z M 6,2 V 14", nameof(L.ProfileToggleSections));
        this.FindControl<ComboBox>("WorldProfileChoice")!.ItemTemplate = new FuncDataTemplate<ConnectionProfile>((profile, _) => Ui.Text(profile?.Name ?? "", 12));
        var scriptHost = this.FindControl<ContentControl>("ProfileScriptsHost")!;
        var macroHost = this.FindControl<ContentControl>("ProfileMacrosHost")!;
        var agentHost = this.FindControl<ContentControl>("ProfileAgentHost")!;
        var agentViews = new Dictionary<AgentProfileViewModel, AgentSettingsView>();
        var scriptViews = new Dictionary<ProfileAutomationEditor, ScriptLibraryView>();
        var macroViews = new Dictionary<ProfileAutomationEditor, MacroLibraryView>();
        void RefreshEditors()
        {
            if (model.IsAgent)
            {
                if (model.Agent is not { } agent) { agentHost.Content = null; return; }
                if (!agentViews.TryGetValue(agent, out var view)) agentViews.Add(agent, view = new(agent, saveWithDialog: true));
                agentHost.Content = view;
                return;
            }
            if (!model.IsAutomation || model.Automation is not { } editor)
            {
                if (!model.CanEditAutomation) { scriptHost.Content = null; macroHost.Content = null; }
                return;
            }
            if (model.IsScripts)
            {
                if (!scriptViews.TryGetValue(editor, out var view)) scriptViews.Add(editor, view = new(editor.Scripts, saveWithDialog: true));
                scriptHost.Content = view;
            }
            else
            {
                if (!macroViews.TryGetValue(editor, out var view)) macroViews.Add(editor, view = new(editor.Macros, saveWithDialog: true));
                macroHost.Content = view;
            }
        }
        void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(model.SectionIndex) or nameof(model.Automation) or nameof(model.SelectedProfile)) RefreshEditors();
        }
        model.PropertyChanged += ModelChanged;
        RefreshEditors();
        var closeApproved = false;
        var confirming = false;
        void Saved() { closeApproved = true; Close(); }
        model.CloseRequested += Saved;
        model.ConfirmDiscardAsync = ConfirmDiscardAsync;
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control), Command = model.SaveCommand });
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
        this.FindControl<Button>("CancelWorld")!.Click += (_, _) => Close();
        var choice = this.FindControl<ComboBox>("WorldProfileChoice")!;
        var changing = false;
        choice.SelectionChanged += async (_, _) =>
        {
            if (changing || choice.SelectedItem as ConnectionProfile == model.SelectedProfile) return;
            var requested = choice.SelectedItem as ConnectionProfile;
            changing = true;
            choice.SetCurrentValue(ComboBox.SelectedItemProperty, model.SelectedProfile);
            try { await model.SelectProfileAsync(requested); }
            finally { choice.SetCurrentValue(ComboBox.SelectedItemProperty, model.SelectedProfile); changing = false; }
        };
        this.FindControl<TextBox>("WorldHost")!.LostFocus += (_, _) => model.NormalizeAddress();
        Closing += async (_, args) =>
        {
            if (closeApproved) return;
            if (model.IsBusy || confirming) { args.Cancel = true; return; }
            if (!model.HasUnsavedChanges) return;
            args.Cancel = true; confirming = true;
            try
            {
                if (await model.CanCloseAsync()) { closeApproved = true; Close(); }
            }
            finally { confirming = false; }
        };
        Closed += (_, _) => { model.PropertyChanged -= ModelChanged; model.CloseRequested -= Saved; model.ConfirmDiscardAsync = null; model.Dispose(); };
    }

    private Task<bool> ConfirmDiscardAsync()
    {
        var dialog = new Window { Title = L.ProfileDiscardTitle, Width = 440, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var keep = Ui.ButtonKey(nameof(L.ProfileKeepEditing), () => dialog.Close(false), "primary");
        keep.Name = "KeepEditing"; keep.IsDefault = true; keep.IsCancel = true;
        var discard = Ui.ButtonKey(nameof(L.ProfileDiscard), () => dialog.Close(true));
        discard.Name = "DiscardChanges";
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 20, Children =
        {
            Ui.TextKey(nameof(L.ProfileDiscardMessage), 14),
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8, Children = { discard, keep } }
        } };
        return dialog.ShowDialog<bool>(this);
    }
}
