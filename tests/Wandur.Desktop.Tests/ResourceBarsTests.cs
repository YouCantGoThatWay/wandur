using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Wandur.Desktop.Views;
using Wandur.Models;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Threading;
using Wandur.Core.Settings;

namespace Wandur.Desktop.Tests;

public sealed class ResourceBarsTests
{
    [AvaloniaFact]
    public async Task LivePacketsUpdateVisibleBarsWhilePrivacyAndReconnectPreventLeakingOtherState()
    {
        var path=Path.Combine(Path.GetTempPath(),"wandur-resource-session-"+Guid.NewGuid());
        Directory.CreateDirectory(path);
        using var server=new TcpListener(IPAddress.Loopback,0); server.Start();
        var port=((IPEndPoint)server.LocalEndpoint).Port;
        var map=new WorldMapping {WorldId="test",Endpoint=new("127.0.0.1",port),SchemaFingerprint=new('a',64),GeneratedAt=DateTimeOffset.UtcNow,
            Bindings=[new() {Source=new("GMCP","Char.Vitals","/hp"),Target=new("character","resource","health","current"),Label="Health"},
                new() {Source=new("GMCP","Char.Vitals","/maxhp"),Target=new("character","resource","health","maximum"),Label="Health"}]};
        var profile=new ConnectionProfile {Name="Resource preview",Host="127.0.0.1",Port=port,ProtocolMapping=map};
        await using var controller=new WorkspaceController(new Wandur.Desktop.Terminal.TranscriptDisplayFactory(),new SettingsStore(Path.Combine(path,"settings.json")),
            new MemoryPasswordVault(),new MemoryRoomMapStore(),new RecordingScriptFactory(),new MemoryScriptLibraryStore());
        var terminal=new TerminalView(controller);
        var window=new Window {Content=terminal,Width=960,Height=520};
        try
        {
            window.Show(); await controller.StartAsync(profile);
            using var socket=await server.AcceptTcpClientAsync();
            async Task Send(string json)
            {
                var packet=new byte[]{255,250,201}.Concat(Encoding.UTF8.GetBytes("Char.Vitals "+json)).Concat(new byte[]{255,240}).ToArray();
                await socket.GetStream().WriteAsync(packet);
                for(var i=0;i<10;i++) {await Task.Delay(10);Dispatcher.UIThread.RunJobs();controller.FlushOutput();}
                window.UpdateLayout();
            }
            await socket.GetStream().WriteAsync(new byte[]{255,251,201});
            await Send("{\"hp\":70}");
            var strip=Assert.Single(terminal.GetVisualDescendants().OfType<ResourceBarsView>());
            Assert.False(strip.IsVisible);
            await Send("{\"maxhp\":100}");
            Assert.True(strip.IsVisible);
            Assert.Equal(70,Assert.Single(strip.GetVisualDescendants().OfType<ProgressBar>()).Value);
            controller.SetManualPrivate(true); await Send("{\"hp\":5}"); controller.SetManualPrivate(false);
            Assert.Equal(70,Assert.Single(strip.GetVisualDescendants().OfType<ProgressBar>()).Value);
            await Send("{\"hp\":23}");
            Assert.Equal(23,Assert.Single(strip.GetVisualDescendants().OfType<ProgressBar>()).Value);
            controller.Terminal.AppendLocalText("\n  Resource preview using simulated protocol data.\n\n  Health updates arrive through the connection's GMCP mapping.\n");
            controller.ShowNotice(null);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            if(Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } captures)
            {Directory.CreateDirectory(captures);using var frame=window.CaptureRenderedFrame();frame?.Save(Path.Combine(captures,"resource-bars-session.png"),new Avalonia.Media.Imaging.PngBitmapEncoderOptions());}
            await controller.DisconnectAsync(); Assert.False(strip.IsVisible);
            await controller.StartAsync(profile);
            using var second=await server.AcceptTcpClientAsync(); Assert.False(strip.IsVisible);
        }
        finally {window.Close();}
    }

    private static ResourceState Resource(string label,double? current,double? maximum) => new(label,
        current is { } c ? new(c,DateTimeOffset.UtcNow) : null,
        maximum is { } m ? new(m,DateTimeOffset.UtcNow) : null);

    [AvaloniaFact]
    public void OnlyValidPairsRenderAndDisconnectOrEmptyStateHidesTheStrip()
    {
        var state=new GameState();
        state.Character.Resources["health"]=Resource("Health",42,100);
        state.Character.Resources["fuel"]=Resource("Fuel",7,null);
        state.Character.Resources["shield"]=Resource("Shield",0,0);
        state.Character.Resources["bad"]=Resource("Bad",double.NaN,100);
        state.Character.Progression["level"]=Resource("Level",20,null);
        state.Character.Progression["experience"]=Resource("Experience",250,1000);
        state.Vehicle.Resources["hull"]=Resource("Hull",120,100);
        var view=new ResourceBarsView(); var window=new Window {Content=view,Width=750,Height=180};
        try
        {
            window.Show(); view.Update(state,true); window.UpdateLayout();
            var bars=view.GetVisualDescendants().OfType<ProgressBar>().ToArray();
            Assert.Equal(3,bars.Length);
            Assert.Contains(bars,b=>b.Value==42);
            Assert.Contains(bars,b=>b.Value==25);
            Assert.Contains(bars,b=>b.Value==100);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text=="120 / 100");
            state.Character.Resources["health"]=Resource("Health",0,100);
            view.Update(state,true); window.UpdateLayout();
            Assert.Contains(view.GetVisualDescendants().OfType<ProgressBar>(),b=>b.Value==0);
            view.Update(state,false); Assert.False(view.IsVisible);
            view.Update(new(),true); Assert.False(view.IsVisible);
        }
        finally {window.Close();}
    }

    [AvaloniaTheory]
    [InlineData(360)]
    [InlineData(960)]
    public void ResourceStripWrapsWithinTheAvailableWidthAndRenders(int width)
    {
        var state=new GameState();
        state.Character.Resources["health"]=Resource("Health",640,640);
        state.Character.Resources["mana"]=Resource("Mana",238,326);
        state.Character.Resources["movement"]=Resource("Movement",189,321);
        state.Opponent.Resources["health"]=Resource("Health",120,400);
        state.Vehicle.Resources["hull"]=Resource("Hull",930,1200);
        state.Character.Progression["experience"]=Resource("Experience",270,495);
        var view=new ResourceBarsView();
        var window=new Window {Content=new DockPanel {Children={view}},Width=width,Height=240};
        try
        {
            window.Show(); view.Update(state,true); window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            var bars=view.GetVisualDescendants().OfType<ProgressBar>().ToArray();
            Assert.Equal(6,bars.Length);
            Assert.All(bars,b=>Assert.InRange(b.Bounds.Width,20,width-10));
            Assert.InRange(view.Bounds.Height,1,148);
            using var frame=window.CaptureRenderedFrame(); Assert.NotNull(frame);
            if(Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } path)
            {Directory.CreateDirectory(path);frame.Save(Path.Combine(path,$"resource-bars-{width}.png"),new Avalonia.Media.Imaging.PngBitmapEncoderOptions());}
        }
        finally {window.Close();}
    }
}
