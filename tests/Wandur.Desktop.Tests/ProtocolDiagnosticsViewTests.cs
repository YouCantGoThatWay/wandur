using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Settings;
using Wandur.Desktop;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

public sealed class ProtocolDiagnosticsViewTests
{
    private static WorkspaceController Controller() => new(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),
        new SettingsStore(Path.Combine(Path.GetTempPath(), "wandur-diagnostics-" + Guid.NewGuid(), "settings.json")),
        new MemoryPasswordVault(), new MemoryRoomMapStore(), new RecordingScriptFactory(), new MemoryScriptLibraryStore());

    [AvaloniaFact]
    public async Task TabsKeepTerminalAliveAndDiagnosticsCanPauseAndClearIndependently()
    {
        await using var controller = Controller();
        var view = new TerminalView(controller);
        var window = new Window { Content = view, Width = 900, Height = 650 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            controller.Terminal.Append("Welcome aboard.\r\n");
            var terminalTab = view.GetVisualDescendants().OfType<TabStripItem>().Single(b => b.Name == "TerminalViewTab");
            var diagnosticTab = view.GetVisualDescendants().OfType<TabStripItem>().Single(b => b.Name == "DiagnosticsViewTab");
            var diagnostics = view.GetVisualDescendants().OfType<ProtocolDiagnosticsView>().Single();
            Assert.True(terminalTab.IsSelected); Assert.False(diagnostics.IsVisible);
            void Click(Control target)
            {
                var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            }
            Click(diagnosticTab); Click(diagnosticTab);
            Assert.True(diagnosticTab.IsSelected); Assert.True(diagnostics.IsVisible);
            var input = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "CommandInput");
            Assert.False(input.IsEffectivelyVisible);
            controller.Terminal.Append("Still receiving while diagnostics is open.\r\n");
            controller.Diagnostics.Append(DateTimeOffset.Now, 201, Encoding.UTF8.GetBytes("Room.Info {\"name\":\"The Cockpit\",\"exits\":[\"south\"]}"));
            controller.Diagnostics.Append(DateTimeOffset.Now, 69, Encoding.UTF8.GetBytes("\u0001HEALTH\u0002" + "85\u0001HEALTH_MAX\u0002" + "100"));
            var list = diagnostics.GetVisualDescendants().OfType<ListBox>().Single();
            list.SelectedItem = controller.Diagnostics.Entries[0];
            Assert.False(controller.Diagnostics.Follow);
            controller.Diagnostics.Append(DateTimeOffset.Now, 201, Encoding.UTF8.GetBytes("Char.Vitals {\"hp\":85}"));
            Assert.Equal(controller.Diagnostics.Entries[0], controller.Diagnostics.SelectedEntry);
            Assert.Contains("The Cockpit", controller.Diagnostics.Detail);
            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            var folder = Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR");
            if (!string.IsNullOrEmpty(folder)) { Directory.CreateDirectory(folder); frame.Save(Path.Combine(folder, "protocol-diagnostics.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
            var schemaTab = diagnostics.GetVisualDescendants().OfType<TabItem>().Single(t => t.Name == "ProtocolSchemaTab");
            Click(schemaTab); Dispatcher.UIThread.RunJobs();
            var schema = diagnostics.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ProtocolSchemaDetail");
            Assert.True(schema.IsEffectivelyVisible);
            Assert.Contains("/hp", schema.Text); Assert.Contains("/HEALTH_MAX", schema.Text);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var schemaFrame = window.CaptureRenderedFrame(); Assert.NotNull(schemaFrame);
            if (!string.IsNullOrEmpty(folder)) schemaFrame.Save(Path.Combine(folder, "protocol-fields.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            controller.Diagnostics.ClearCommand.Execute(null);
            Assert.True(controller.Diagnostics.IsEmpty);
            Assert.Contains("Still receiving", controller.Display.PlainText);
            Click(terminalTab);
            Assert.False(diagnostics.IsVisible); Assert.True(terminalTab.IsSelected);
            Assert.True(input.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LoginResponsesShowReadableDetailsAndRedactionNotice()
    {
        var model = new ProtocolDiagnosticsViewModel();
        var view = new ProtocolDiagnosticsView(model);
        var window = new Window { Content = view, Width = 900, Height = 620 };
        try
        {
            window.Show();
            model.Append(DateTimeOffset.Now, 201, Encoding.UTF8.GetBytes("Char.Login.Default {\"type\":[\"password-credentials\"]}"));
            model.Append(DateTimeOffset.Now, 201, Encoding.UTF8.GetBytes("Char.Login.Result {\"success\":false,\"message\":\"Account not found\",\"token\":\"fixture-secret\"}"));
            Dispatcher.UIThread.RunJobs();
            var detail = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ProtocolMessageDetail");
            Assert.Contains("Account not found", detail.Text);
            Assert.Contains("redacted", detail.Text);
            Assert.DoesNotContain("fixture-secret", detail.Text);
            Assert.DoesNotContain("Payload hidden", detail.Text);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            var folder = Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR");
            if (!string.IsNullOrEmpty(folder)) { Directory.CreateDirectory(folder); frame.Save(Path.Combine(folder, "login-diagnostics.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
        }
        finally { window.Close(); }
    }

    [Fact]
    public void SchemaSurvivesHistoryEvictionExcludesPrivateDataAndClearsExplicitly()
    {
        var model = new ProtocolDiagnosticsViewModel();
        model.Append(DateTimeOffset.Now, 201, Encoding.UTF8.GetBytes("Char.Vitals {\"hp\":42}"));
        model.Append(DateTimeOffset.Now, 201, null);
        for (int i = 0; i < 220; i++) model.Append(DateTimeOffset.Now, 69, Encoding.UTF8.GetBytes("\u0001HEALTH\u000285"));
        Assert.DoesNotContain(model.Entries, e => e.Content?.Name == "Char.Vitals");
        Assert.Contains("Char.Vitals", model.SchemaDetail);
        Assert.Contains("/HEALTH", model.SchemaDetail);
        Assert.DoesNotContain("private", model.SchemaDetail, StringComparison.OrdinalIgnoreCase);
        model.ClearCommand.Execute(null);
        Assert.DoesNotContain("Char.Vitals", model.SchemaDetail);
        Assert.DoesNotContain("/HEALTH", model.SchemaDetail);
    }

    [Fact]
    public void HistoryIsBoundedAndEvictedSelectionDoesNotRetainPayloads()
    {
        var model = new ProtocolDiagnosticsViewModel();
        for (var i = 0; i < 220; i++) model.Append(DateTimeOffset.Now, 201, Encoding.UTF8.GetBytes($"Char.Vitals {{\"hp\":{i}}}"));
        Assert.Equal(ProtocolDiagnosticsViewModel.MaximumEntries, model.Entries.Count);
        model.Follow = false; model.SelectedEntry = model.Entries[0];
        for (var i = 0; i < 100; i++) model.Append(DateTimeOffset.Now, 201, Encoding.UTF8.GetBytes("Data \"" + new string('x', 16000) + "\""));
        Assert.Null(model.SelectedEntry);
        Assert.True(model.Entries.Sum(e => e.Content?.Body.Length ?? 0) <= ProtocolDiagnosticsViewModel.MaximumCharacters);
    }

    [AvaloniaFact]
    public async Task CapturesBothProtocolsInWireOrderWithPrivacyAndReconnectIsolation()
    {
        await using var controller = Controller();
        await using var other = Controller();
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        var profile = new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)server.LocalEndpoint).Port };
        await controller.StartAsync(profile);
        using var socket = await server.AcceptTcpClientAsync();
        static byte[] Message(byte option, string text) => new byte[] { 255, 250, option }.Concat(Encoding.UTF8.GetBytes(text)).Concat(new byte[] { 255, 240 }).ToArray();
        async Task Output(byte[] bytes)
        {
            await socket.GetStream().WriteAsync(bytes);
            for (var i = 0; i < 10; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); controller.FlushOutput(); }
        }
        await Output(new byte[] { 255, 251, 201, 255, 251, 69 }
            .Concat(Message(201, "Char.Vitals {\"hp\":42}"))
            .Concat(Message(69, "\u0001HEALTH\u0002" + "42")).ToArray());
        Assert.Equal(new[] { "GMCP", "MSDP" }, controller.Diagnostics.Entries.Select(e => e.Protocol));
        Assert.Contains("42", controller.Diagnostics.Entries[1].Content!.Body);
        Assert.Empty(other.Diagnostics.Entries);
        controller.SetManualPrivate(true);
        await Output(Message(201, "Auth.Info {\"password\":\"hidden-secret\"}"));
        Assert.NotNull(controller.Diagnostics.Entries[^1].Content);
        Assert.Contains("[redacted]", controller.Diagnostics.Entries[^1].Content!.Body);
        controller.SetManualPrivate(false);
        // Privacy must survive an unfinished subnegotiation, including an interval with no reads.
        var split = Message(201, "Auth.Info {\"password\":\"fragment-secret\"}");
        await Output(split[..^2]);
        controller.SetManualPrivate(true);
        controller.SetManualPrivate(false);
        await Output(split[^2..]);
        Assert.NotNull(controller.Diagnostics.Entries[^1].Content);
        Assert.Contains("[redacted]", controller.Diagnostics.Entries[^1].Content!.Body);
        // Entering and leaving server-side private input within a single read must also be hidden.
        await Output(new byte[] { 255, 251, 1 }.Concat(Message(69, "\u0001PASSWORD\u0002hidden-secret")).Concat(new byte[] { 255, 252, 1 }).ToArray());
        Assert.NotNull(controller.Diagnostics.Entries[^1].Content);
        Assert.Contains("[redacted]", controller.Diagnostics.Entries[^1].Content!.Body);
        Assert.DoesNotContain(controller.Diagnostics.Entries, e => e.Content?.Body.Contains("hidden-secret") == true);
        await controller.DisconnectAsync();
        Assert.Equal(5, controller.Diagnostics.Entries.Count);
        await controller.StartAsync(profile);
        using var secondSocket = await server.AcceptTcpClientAsync();
        Assert.Empty(controller.Diagnostics.Entries);
    }

    [AvaloniaFact]
    public async Task DiagnosticBurstCannotDiscardGameplayOutput()
    {
        await using var controller = Controller();
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        await controller.StartAsync(new ConnectionProfile { Host = "127.0.0.1", Port = ((IPEndPoint)server.LocalEndpoint).Port });
        using var socket = await server.AcceptTcpClientAsync();
        controller.Terminal.Append("Previous transcript.\r\n");
        var message = new byte[] { 255, 250, 69 }.Concat(Encoding.UTF8.GetBytes("\u0001STATUS\u0002" + new string('x', 300))).Concat(new byte[] { 255, 240 }).ToArray();
        var bytes = new byte[] { 255, 251, 69 }.Concat(Encoding.UTF8.GetBytes("Queued gameplay text.\r\n"))
            .Concat(Enumerable.Range(0, 2500).SelectMany(_ => message)).Concat(new byte[] { 255, 253, 24 }).ToArray();
        // Keep the UI flush timer paused while the receive thread handles the entire burst.
        Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.GetStream().WriteAsync(bytes, timeout.Token);
            var response = new List<byte>(); var buffer = new byte[4096];
            while (true)
            {
                var count = await socket.GetStream().ReadAsync(buffer, timeout.Token);
                Assert.True(count > 0);
                response.AddRange(buffer.Take(count));
                if (Enumerable.Range(0, Math.Max(0, response.Count - 2)).Any(i => response[i] == 255 && response[i + 1] == 251 && response[i + 2] == 24)) break;
            }
        }).GetAwaiter().GetResult();
        controller.FlushOutput();
        Assert.Contains("Previous transcript.", controller.Display.PlainText);
        Assert.Contains("Queued gameplay text.", controller.Display.PlainText);
        Assert.Null(controller.Notice);
        Assert.InRange(controller.Diagnostics.Entries.Count, 1, ProtocolDiagnosticsViewModel.MaximumEntries);
    }
}
