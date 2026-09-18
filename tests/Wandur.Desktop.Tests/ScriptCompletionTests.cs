using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class ScriptCompletionTests
{
    [Theory]
    [InlineData("mud.", "send", "echo")]
    [InlineData("Events.", "Line", "Gmcp")]
    [InlineData("mud.on(Events.Line, message => { message.", "text", "text")]
    [InlineData("mud.on(Events.Gmcp, packet => { packet.", "data", "package")]
    public void CatalogOffersContextualApiMembers(string source, string first, string second)
    {
        var suggestions = ScriptCompletionCatalog.Get(source).Suggestions.Select(s => s.Name);
        Assert.Contains(first, suggestions); Assert.Contains(second, suggestions);
    }

    [Theory]
    [InlineData("player.")]
    [InlineData("mud.on(Events.Gmcp, packet => { packet.data.")]
    public void UnknownObjectsDoNotPretendToHaveMudApiMembers(string source)
        => Assert.Empty(ScriptCompletionCatalog.Get(source).Suggestions);

    [Fact]
    public void LongOrMalformedContextsRemainBounded()
    {
        foreach (var text in new[] { new string('a', 300_000) + ";", new string(' ', 300_000) + "mud.", string.Concat(Enumerable.Repeat("mud.on(Events.Line, ", 1000)) })
            Assert.NotNull(ScriptCompletionCatalog.Get(text));
    }

    [AvaloniaFact]
    public void DotOpensEnumCompletionAndTabInsertsOnlyTheMember()
    {
        var editor = new ScriptCodeEditor { SourceText = "mud.on(Events" };
        var window = new Window { Width = 850, Height = 450, Content = editor };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); editor.TextArea.Focus(); editor.CaretOffset = editor.Text.Length;
            window.KeyTextInput("."); Dispatcher.UIThread.RunJobs();
            Assert.NotNull(editor.Completion); Assert.True(editor.Completion.IsOpen);
            Assert.Equal(new[] { "Line", "Gmcp" }, editor.Completion.CompletionList.CompletionData.Select(d => d.Text));
            window.KeyTextInput("Li"); window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("mud.on(Events.Line", editor.Text); Assert.Equal(editor.Text, editor.SourceText); Assert.Null(editor.Completion);
            Assert.True(editor.Document.UndoStack.CanUndo);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ManualCompletionReplacesPartialMemberAndEscapePreservesText()
    {
        var editor = new ScriptCodeEditor { SourceText = "mud.se" };
        var window = new Window { Width = 850, Height = 450, Content = editor };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); editor.TextArea.Focus(); editor.CaretOffset = editor.Text.Length;
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control); window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.Control); Dispatcher.UIThread.RunJobs();
            Assert.NotNull(editor.Completion);
            Assert.Equal("send", Assert.Single(editor.Completion.CompletionList.CompletionData).Text);
            Assert.IsType<TextBlock>(editor.Completion.CompletionList.CompletionData[0].Description);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None); Dispatcher.UIThread.RunJobs();
            Assert.Equal("mud.send", editor.Text);
            editor.SourceText = "Events."; editor.CaretOffset = editor.Text.Length; editor.ShowCompletion();
            Assert.NotNull(editor.Completion);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None); Dispatcher.UIThread.RunJobs();
            Assert.Equal("Events.", editor.Text); Assert.Null(editor.Completion);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("// mud.")]
    [InlineData("/* Events.")]
    [InlineData("const text = 'mud.")]
    [InlineData("const text = `Events.")]
    public void CompletionDoesNotOpenInCommentsOrStrings(string text)
    {
        var editor = new ScriptCodeEditor { SourceText = text };
        var window = new Window { Width = 850, Height = 450, Content = editor };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); editor.TextArea.Focus(); editor.CaretOffset = editor.Text.Length; editor.ShowCompletion();
            Assert.Null(editor.Completion);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ChangingSourceClosesCompletionBeforeSwitchingScripts()
    {
        var editor = new ScriptCodeEditor { SourceText = "Events." };
        var window = new Window { Width = 850, Height = 450, Content = editor };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); editor.TextArea.Focus(); editor.CaretOffset = editor.Text.Length; editor.ShowCompletion();
            Assert.NotNull(editor.Completion);
            editor.SourceText = "mud.echo('another script');";
            Assert.Null(editor.Completion); Assert.Equal(editor.SourceText, editor.Text);
        }
        finally { window.Close(); }
    }
}
