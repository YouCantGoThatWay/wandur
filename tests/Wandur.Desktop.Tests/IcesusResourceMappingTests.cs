using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Wandur.Core.Protocol;
using Wandur.Desktop.Views;
using Wandur.Models;

namespace Wandur.Desktop.Tests;

public sealed class IcesusResourceMappingTests
{
    [AvaloniaFact]
    public void PublishedIcesusMapCombinesVitalsAndMaxstatsWithoutGuessingUnsentValues()
    {
        var catalog=JsonSerializer.Deserialize<MappingCatalog>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures/curated-mappings.json")),ModelJson.Options)!;
        var mapping=Assert.Single(catalog.Worlds,w=>w.WorldId=="mudverse:645");
        Assert.True(MappingValidation.IsValid(mapping));
        var engine=new ProtocolBindingEngine(mapping);
        void Packet(string message)=>engine.Observe(201,ProtocolDiagnosticFormatter.Format(201,Encoding.UTF8.GetBytes(message)),DateTimeOffset.UtcNow);
        Packet("Char.Base {\"name\":\"Test character\"}");
        Packet("Char.Vitals {\"hp\":640,\"mana\":163,\"moves\":107,\"psp\":0}");
        var view=new ResourceBarsView(); var window=new Window {Content=view,Width=800,Height=180};
        try
        {
            window.Show(); view.Update(engine.State,true); Assert.False(view.IsVisible);
            Packet("Char.Maxstats {\"maxhp\":640,\"maxmana\":326,\"maxmoves\":321,\"maxpsp\":0}");
            view.Update(engine.State,true);window.UpdateLayout();
            var bars=view.GetVisualDescendants().OfType<ProgressBar>().ToArray();
            Assert.Equal(3,bars.Length);
            Assert.Contains(bars,b=>b.Value==100); Assert.Contains(bars,b=>b.Value==50);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text=="Spell points (SP)");
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text=="Endurance (EP)");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            if(Environment.GetEnvironmentVariable("WANDUR_CAPTURE_DIR") is { } path)
            {Directory.CreateDirectory(path);using var frame=window.CaptureRenderedFrame();frame?.Save(Path.Combine(path,"icesus-resource-bars.png"),new Avalonia.Media.Imaging.PngBitmapEncoderOptions());}
            Packet("Char.Vitals {\"mana\":81.5}"); view.Update(engine.State,true);window.UpdateLayout();
            Assert.Contains(view.GetVisualDescendants().OfType<ProgressBar>(),b=>b.Value==25);
            Packet("Char.Base {\"name\":\"Different character\"}");view.Update(engine.State,true);Assert.False(view.IsVisible);
        }
        finally {window.Close();}
    }
}
