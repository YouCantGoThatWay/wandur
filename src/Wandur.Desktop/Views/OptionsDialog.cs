using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Wandur.Core.Localization;
using Avalonia.Markup.Xaml;
using Wandur.Desktop.ViewModels;

namespace Wandur.Desktop.Views;

public sealed partial class OptionsDialog : Window
{
    public OptionsDialog()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ComboBox>("LanguageChoice")!.ItemTemplate = new FuncDataTemplate<LanguageChoice>((choice, _) =>
            choice is null ? null : choice.Code.Length == 0 ? Ui.TextKey(nameof(Strings.SystemLanguage)) : Ui.Text(choice.Name));
    }
    public OptionsDialog(PreferencesViewModel model) : this()
    {
        DataContext = model;
        model.CloseRequested += Close;
        this.FindControl<Button>("CancelPreferences")!.Click += (_, _) => Close();
        Closed += (_, _) => { model.CloseRequested -= Close; model.Dispose(); };
    }
}
