using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Channels;
using Wandur.Core.Settings;
using Wandur.Desktop.Terminal;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

/// <summary>
/// Teaching a world its channels from the transcript: a right click, a proposal, a preview, and a rule on the
/// profile that the Channels panel uses for the very next line. The world opens as SMAUG, so the family
/// knows [OOC] and knows nothing of [CLAN].
/// </summary>
public sealed class TeachChannelTests
{
    private static MudTerminalSurface Surface(Window window) => Assert.Single(window.GetVisualDescendants().OfType<MudTerminalSurface>());
    private static MenuItem Item(ContextMenu menu, string name) => Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Name == name);
    private static Point RowPoint(MudTerminalSurface surface, Window window, int row)
        => surface.TranslatePoint(new Point(30, surface.CharHeight * (row + 0.5)), window)!.Value;

    [AvaloniaFact]
    public async Task ARightClickOffersToMarkTheLineAndTheDialogProposesARuleWithAPreview()
    {
        await using var world = await World.Open();
        var view = new TerminalView(world.Controller);
        var window = new Window { Width = 900, Height = 600, Content = view };
        MarkChannelDialog? dialog = null;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await world.Output("You are standing in a wide green field.\r\n[CLAN] Vex: meeting at dawn\r\n[CLAN] Talon: bring the ship\r\nA small bird lands nearby.\r\n[CLAN] Vex: and the crew\r\n");
            window.UpdateLayout();
            var surface = Surface(window);
            Assert.True(surface.CharHeight > 0);
            Assert.Equal("[CLAN] Vex: meeting at dawn", surface.LineAt(surface.CharHeight * 1.5));

            // A right click on the second row: no selection, so no copy, and the line can be marked.
            var point = RowPoint(surface, window, 1);
            window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            var menu = view.TranscriptMenu;
            Assert.NotNull(menu);
            Assert.True(menu.IsOpen);
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => item.Name == "TranscriptCopyMenu");
            var mark = Item(menu, "MarkChannelMenu");
            Assert.True(mark.Command!.CanExecute(null));
            mark.Command.Execute(null);
            menu.Close();
            Dispatcher.UIThread.RunJobs();

            dialog = Assert.Single(window.OwnedWindows.OfType<MarkChannelDialog>());
            var model = dialog.Model;
            Assert.Equal("[CLAN] Vex: meeting at dawn", model.Example);
            Assert.Equal("\\[CLAN\\] ", model.HeadPattern);
            Assert.Equal("@?[A-Za-z]+", model.SpeakerPattern);
            Assert.Equal(": ", model.SeparatorPattern);
            Assert.Equal("^\\[CLAN\\] (?<speaker>@?[A-Za-z]+): (?<text>.*)$", model.Pattern);
            // SMAUG knows no clan channel, so the picker lands on a new channel named by the head.
            Assert.True(model.IsNewChannel);
            Assert.Equal("clan", model.NewChannelName);
            Assert.Equal("clan", model.Channel);
            Assert.Equal("clan", model.ReplyCommand);
            Assert.Contains("ooc", model.ChannelChoices);
            Assert.Equal(Wandur.Core.Localization.Strings.TeachChannelNewChannel, model.ChannelChoices[^1]);
            Assert.Null(model.CurrentChannel);
            Assert.False(model.CanExclude);
            Assert.Equal(3, model.MatchCount);
            Assert.Equal(["Vex: meeting at dawn", "Talon: bring the ship", "Vex: and the crew"], model.Matches.Select(m => m.Label));
            Assert.True(model.CanSave);
            dialog.UpdateLayout();
            Assert.Equal(3, Assert.Single(dialog.GetVisualDescendants().OfType<ItemsControl>(), c => c.Name == "TeachPreviewMatches").ItemCount);
            Assert.Contains("3", Assert.Single(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "TeachPreviewSummary").Text);
            Assert.False(Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Name == "TeachExclude").IsVisible);
            if (Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } folder)
            {
                Directory.CreateDirectory(folder); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = HeadlessWindowExtensions.CaptureRenderedFrame(dialog);
                Assert.NotNull(frame); frame.Save(Path.Combine(folder, "teach-channel.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }

            // Editing a piece recomposes the rule and the preview follows; a head that fits nothing says so.
            model.HeadPattern = "\\[SHIP\\] ";
            Assert.Equal("^\\[SHIP\\] (?<speaker>@?[A-Za-z]+): (?<text>.*)$", model.Pattern);
            Assert.Equal(0, model.MatchCount);
            Assert.Equal(Wandur.Core.Localization.Strings.TeachChannelPreviewNone, model.PreviewSummary);
            model.HeadPattern = "\\[CLAN\\] ";
            Assert.Equal(3, model.MatchCount);
            // The rule can be edited directly; the pieces are left alone and a broken one cannot be saved.
            model.Pattern = "^\\[CLAN\\] (?<speaker>[A-Za-z";
            Assert.False(model.CanSave);
            Assert.Equal(Wandur.Core.Localization.Strings.TeachChannelInvalidPattern, model.Error);
            model.Pattern = "^\\[CLAN\\] (?<speaker>@?[A-Za-z]+): (?<text>.*)$";
            Assert.True(model.CanSave);
            Assert.Equal("\\[CLAN\\] ", model.HeadPattern);

            // Saving writes the rule to the profile through the settings store and it is in force at once.
            var channels = new ChannelsViewModel(world.Controller); channels.Attach();
            model.SaveCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.OwnedWindows.OfType<MarkChannelDialog>());
            var saved = Assert.Single(world.Settings.Load().Settings.Profiles);
            var rule = Assert.Single(saved.ChannelRules);
            Assert.Equal(new ChannelRule("clan", "^\\[CLAN\\] (?<speaker>@?[A-Za-z]+): (?<text>.*)$", "clan"), rule);
            Assert.Equal("clan", world.Controller.ChannelRules.Rules[0].Channel);
            Assert.Contains("clan", world.Controller.ChannelRules.Channels);
            Assert.Contains("clan", Assert.Single(world.Controller.ActiveProfile!.ChannelRules).Channel);
            await world.Output("[CLAN] Talon: docking now\r\n[OOC] Aldric: still here\r\n");
            var clan = Assert.Single(channels.Tabs, t => t.Channel == "clan");
            var message = Assert.Single(clan.Messages);
            Assert.Equal("Talon", message.Speaker);
            Assert.Equal("docking now", message.Text);
            Assert.Equal("clan", message.ReplyCommand);
            // The family still does its part beside the taught rule.
            Assert.Single(Assert.Single(channels.Tabs, t => t.Channel == "ooc").Messages);
            Assert.Contains("[CLAN] Talon: docking now", world.Controller.Terminal.PlainText);
            channels.Detach();
        }
        finally { dialog?.Close(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task ASelectionOffersCopyAndTheLiveViewOffersTheSameMenu()
    {
        await using var world = await World.Open();
        var view = new TerminalView(world.Controller);
        var window = new Window { Width = 900, Height = 600, Content = view };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var lines = new StringBuilder();
            for (var i = 0; i < 40; i++) lines.Append($"[CLAN] Vex: message number {i} of the evening\r\n");
            await world.Output(lines.ToString());
            window.UpdateLayout();
            var surface = Surface(window);
            surface.ViewportY = 0; Dispatcher.UIThread.RunJobs();
            var start = RowPoint(surface, window, 2);
            var end = start + new Vector(220, 0);
            window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(60, 0)); window.MouseMove(end); window.MouseUp(end, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(world.Controller.Display.HasSelection);
            window.MouseDown(start, MouseButton.Right); window.MouseUp(start, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            var menu = view.TranscriptMenu;
            Assert.NotNull(menu);
            Assert.NotNull(Item(menu, "TranscriptCopyMenu").Command);
            Assert.NotNull(Item(menu, "MarkChannelMenu").Command);
            // The right click did not take the selection away, which is what the library would have done.
            Assert.True(world.Controller.Display.HasSelection);
            menu.Close(); Dispatcher.UIThread.RunJobs();

            // Scrolled back, the live view below the divider answers a right click with the same menu.
            world.Controller.ApplySettings(world.Controller.Settings with { ScrollTailShare = 0.3 });
            surface.ViewportY = 1; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var tail = Assert.Single(window.GetVisualDescendants().OfType<TranscriptTailPane>());
            Assert.True(tail.IsVisible);
            Assert.True(tail.Rows.Count > 0);
            var lastRow = tail.GetVisualDescendants().OfType<TextBlock>().Last(b => b.Bounds.Height > 0);
            var onRow = lastRow.TranslatePoint(new Point(10, lastRow.Bounds.Height / 2), window)!.Value;
            window.MouseDown(onRow, MouseButton.Right); window.MouseUp(onRow, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            var tailMenu = view.TranscriptMenu;
            Assert.NotNull(tailMenu);
            Assert.NotSame(menu, tailMenu);
            Assert.NotNull(Item(tailMenu, "MarkChannelMenu").Command);
            tailMenu.Close();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task NotAChannelTeachesAnExclusionThatBeatsTheFamilyRule()
    {
        await using var world = await World.Open();
        var view = new TerminalView(world.Controller);
        var window = new Window { Width = 900, Height = 600, Content = view };
        MarkChannelDialog? dialog = null;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var channels = new ChannelsViewModel(world.Controller); channels.Attach();
            await world.Output("[OOC] Aldric: anyone selling a lantern?\r\n");
            Assert.Single(channels.Tabs, t => t.Channel == "ooc");
            view.OpenMarkChannel("[OOC] Aldric: anyone selling a lantern?");
            Dispatcher.UIThread.RunJobs();
            dialog = Assert.Single(window.OwnedWindows.OfType<MarkChannelDialog>());
            var model = dialog.Model;
            Assert.Equal("ooc", model.CurrentChannel);
            Assert.True(model.CanExclude);
            Assert.False(model.IsNewChannel);
            Assert.Equal("ooc", model.Channel);
            dialog.UpdateLayout();
            Assert.True(Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Name == "TeachExclude").IsVisible);
            model.ExcludeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.OwnedWindows.OfType<MarkChannelDialog>());
            var rule = Assert.Single(Assert.Single(world.Settings.Load().Settings.Profiles).ChannelRules);
            Assert.True(rule.Exclude);
            Assert.Equal("ooc", rule.Channel);
            Assert.Equal(model.Pattern, rule.Pattern);

            await world.Output("[OOC] Brenna: welcome back\r\n[CHAT] Eowyn: who wants to group up?\r\n");
            Assert.Single(Assert.Single(channels.Tabs, t => t.Channel == "ooc").Messages);
            Assert.Single(Assert.Single(channels.Tabs, t => t.Channel == "chat").Messages);
            Assert.Contains("[OOC] Brenna: welcome back", world.Controller.Terminal.PlainText);
            channels.Detach();
        }
        finally { dialog?.Close(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheWorldEditorListsTogglesAndDeletesTheRules()
    {
        var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-teach-editor-" + Guid.NewGuid(), "settings.json"));
        await using var controller = new WorkspaceController(new TranscriptDisplayFactory(), settings, new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        var profile = new ConnectionProfile { Name = "Taught", Host = "taught.example.org", Codebase = "SMAUG 1.4a", ChannelRules =
            [new("clan", "^\\[CLAN\\] (?<speaker>[A-Za-z]+): (?<text>.*)$", "clan"), new("ooc", "^\\[OOC\\] Aldric: ", Exclude: true)] };
        controller.SaveSettings(controller.Settings with { Profiles = [profile] });
        using var model = new ProfileEditorViewModel(controller, new EmptyDirectory(), profile) { SectionIndex = ProfileEditorViewModel.ChannelsSection };
        var dialog = new ProfileDialog(model);
        try
        {
            dialog.Show(); Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout();
            Assert.True(model.IsChannels);
            Assert.Equal(Wandur.Core.Localization.Strings.ProfileChannelsSection, model.Sections[^1].Label);
            Assert.Equal(2, model.ChannelRules.Count);
            Assert.True(model.HasChannelRules);
            Assert.False(model.HasUnsavedChanges);
            var host = Assert.Single(dialog.GetVisualDescendants().OfType<ContentControl>(), c => c.Name == "ProfileChannelsHost");
            Assert.True(host.IsVisible);
            var deletes = dialog.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "ChannelRuleDelete").ToList();
            Assert.Equal(2, deletes.Count);
            Assert.Equal(2, dialog.GetVisualDescendants().OfType<TextBox>().Count(b => b.Name == "ChannelRulePattern"));
            var kinds = dialog.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "ChannelRuleKind").ToList();
            Assert.Single(kinds, k => k.IsVisible && k.Text == Wandur.Core.Localization.Strings.ProfileChannelExclude);
            Assert.False(Assert.Single(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "ChannelRulesEmpty").IsVisible);

            // Turning a rule off is a change; deleting the exclusion is another; Apply writes both to the profile.
            model.ChannelRules[0].Enabled = false;
            Assert.True(model.HasUnsavedChanges);
            deletes[1].Command!.Execute(deletes[1].CommandParameter);
            Assert.Single(model.ChannelRules);
            await model.ApplyCommand.ExecuteAsync(null);
            Assert.Equal("", model.Error);
            var saved = Assert.Single(settings.Load().Settings.Profiles);
            var rule = Assert.Single(saved.ChannelRules);
            Assert.Equal("clan", rule.Channel);
            Assert.True(rule.Disabled);
            Assert.False(rule.Exclude);
            Assert.Equal("SMAUG 1.4a", saved.Codebase);
            Assert.False(model.HasUnsavedChanges);
            dialog.UpdateLayout();
            Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Name == "ChannelRuleDelete");

            // Editing the pattern to something unreadable is refused with the profile's own message.
            model.ChannelRules[0].Pattern = "(?<speaker>[A-Za-z";
            await model.ApplyCommand.ExecuteAsync(null);
            Assert.Equal(Wandur.Core.Localization.Strings.ChannelRulesInvalid, model.Error);
            Assert.EndsWith("$", Assert.Single(settings.Load().Settings.Profiles).ChannelRules[0].Pattern);

            // The last rule gone, the section says how to teach one.
            var last = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), b => b.Name == "ChannelRuleDelete");
            last.Command!.Execute(last.CommandParameter);
            Assert.Empty(model.ChannelRules);
            dialog.UpdateLayout();
            Assert.True(Assert.Single(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "ChannelRulesEmpty").IsVisible);
            await model.ApplyCommand.ExecuteAsync(null);
            Assert.Empty(Assert.Single(settings.Load().Settings.Profiles).ChannelRules);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task ARuleSavedInTheEditorReachesTheOpenSessionAndTheDemoCannotBeTaught()
    {
        await using var world = await World.Open();
        var channels = new ChannelsViewModel(world.Controller); channels.Attach();
        try
        {
            var profile = Assert.Single(world.Controller.Settings.Profiles);
            var taught = profile with { ChannelRules = [new("clan", "^\\[CLAN\\] (?<speaker>[A-Za-z]+): (?<text>.*)$", "clan")] };
            world.Controller.SaveSettings(world.Controller.Settings with { Profiles = [taught] });
            Assert.Equal("clan", world.Controller.ChannelRules.Rules[0].Channel);
            await world.Output("[CLAN] Vex: meeting at dawn\r\n");
            Assert.Single(Assert.Single(channels.Tabs, t => t.Channel == "clan").Messages);
        }
        finally { channels.Detach(); }

        await using var demo = new WorkspaceController(new TranscriptDisplayFactory(), new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-teach-demo-" + Guid.NewGuid(), "settings.json")), new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
        await demo.StartAsync();
        Assert.Null(demo.ActiveProfile);
        var model = new MarkChannelViewModel(demo, "[CLAN] Vex: meeting at dawn");
        Assert.False(model.CanTeach);
        Assert.False(model.CanSave);
        Assert.Equal(Wandur.Core.Localization.Strings.TeachChannelNoProfile, model.Error);
        Assert.Throws<InvalidOperationException>(() => demo.TeachChannelRule(new("clan", "x")));
    }

    private sealed class EmptyDirectory : Wandur.Core.Discovery.IWorldDirectory
    {
        public Task<Wandur.Core.Discovery.WorldNameSuggestion?> LookupAsync(string host, int port, CancellationToken cancellationToken = default)
            => Task.FromResult<Wandur.Core.Discovery.WorldNameSuggestion?>(null);
    }

    /// <summary>A session against a loopback listener, opened as a saved SMAUG world so the family rules apply.</summary>
    private sealed class World(TcpListener listener, TcpClient socket, WorkspaceController controller, SettingsStore settings) : IAsyncDisposable
    {
        public WorkspaceController Controller => controller;
        public SettingsStore Settings => settings;
        public static async Task<World> Open()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var settings = new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-teach-" + Guid.NewGuid(), "settings.json"));
            var controller = new WorkspaceController(new TranscriptDisplayFactory(), settings,
                new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());
            var profile = new ConnectionProfile { Name = "Realm", Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Codebase = "SMAUG 1.4a" };
            controller.SaveSettings(controller.Settings with { Profiles = [profile] });
            await controller.StartAsync(profile);
            return new(listener, await listener.AcceptTcpClientAsync(), controller, settings);
        }
        public async Task Output(string text)
        {
            await socket.GetStream().WriteAsync(Encoding.UTF8.GetBytes(text));
            for (var i = 0; i < 6; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); controller.FlushOutput(); }
            Dispatcher.UIThread.RunJobs();
        }
        public async ValueTask DisposeAsync() { await controller.DisposeAsync(); socket.Dispose(); listener.Dispose(); }
    }
}
