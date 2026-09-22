using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Wandur.Core.Scripting;
using Wandur.Core.Terminal;
using Wandur.Desktop.Services;
using Wandur.Desktop.Terminal;
using Wandur.Models;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

/// <summary>Shows observed current/maximum pairs without guessing missing capacity. Script panels declared with
/// dock "bars" add their gauges after the mapped vitals, as the same cards.</summary>
public sealed class ResourceBarsView : UserControl
{
    private sealed record Item(string Id,string Label,string Key,double Current,double Maximum,double Percentage);
    private readonly UniformGrid _items=new() {Columns=1};
    private readonly List<IDisposable> _bindings=[];
    private readonly List<Control> _mapped=[];
    private readonly Dictionary<ScriptPanelWidget,Action<bool>> _watched=[];
    private readonly Dictionary<ScriptPanelWidget,ResourceBar> _cards=[];
    private readonly List<ResourceBar> _script=[];
    private Item[] _shown=[];
    private string _culture="";
    private ScriptPanelHost? _panels;

    public ResourceBarsView()
    {
        Name="ResourceBars";
        MaxHeight=144;
        VerticalAlignment=VerticalAlignment.Top;
        IsVisible=false;
        var scroll=new ScrollViewer {Content=_items,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxHeight=136};
        var frame=new Border {Padding=new Thickness(0,4),BorderThickness=new Thickness(0,1,0,0),Child=scroll};
        frame.Bind(Border.BackgroundProperty,new DynamicResourceExtension("ShellBrush"));
        frame.Bind(Border.BorderBrushProperty,new DynamicResourceExtension("LineBrush"));
        Content=frame;
        // Gauge labels may carry color codes; they resolve to the palette brushes the transcript uses.
        TerminalPalette.Bind(this,_bindings);
        SizeChanged+=(_,e)=>_items.Columns=Math.Clamp((int)(Math.Max(0,e.NewSize.Width)/220),1,4);
    }

    /// <summary>The session's script panels. Those declared with dock "bars" show their gauges here, after the mapped vitals.</summary>
    public ScriptPanelHost? Panels
    {
        get=>_panels;
        set
        {
            if(ReferenceEquals(_panels,value)) return;
            if(_panels is not null) _panels.Changed-=SyncScripts;
            _panels=value;
            if(_panels is not null) _panels.Changed+=SyncScripts;
            SyncScripts();
        }
    }

    /// <summary>The gauge cards script panels currently contribute, in strip order.</summary>
    internal IReadOnlyList<ResourceBar> ScriptCards=>_script;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeService.Applied+=Recolor;
        Recolor();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeService.Applied-=Recolor;
        base.OnDetachedFromVisualTree(e);
    }

    public void Update(GameState state,bool connected)
    {
        var items=new List<Item>();
        if(connected)
        {
            Add("character",state.Character,"");
            Add("opponent",state.Opponent,L.VitalsOpponent);
            Add("vehicle",state.Vehicle,L.VitalsVehicle);
            Add("world",state.World,L.VitalsWorld);
        }
        var next=items.ToArray();
        var culture=Wandur.Core.Localization.UiLanguage.Culture.Name;
        if(_shown.SequenceEqual(next) && _culture==culture) return;
        _culture=culture;
        _shown=next;
        _mapped.Clear();
        foreach(var item in next) _mapped.Add(Card(item));
        Compose();

        void Add(string id,EntityState entity,string prefix)
        {
            foreach(var category in new[]{("resource",entity.Resources),("progression",entity.Progression)})
                foreach(var (key,value) in category.Item2.OrderBy(p=>Priority(p.Key)).ThenBy(p=>p.Key,StringComparer.Ordinal))
                {
                    if(value.Current is not {Value:>=0} current || value.Maximum is not {Value:>0} maximum
                        || !double.IsFinite(current.Value) || !double.IsFinite(maximum.Value) || value.Percentage is not { } percent) continue;
                    var label=Label(key,value.Label);
                    items.Add(new(id+":"+category.Item1+":"+key,string.IsNullOrEmpty(prefix)?label:prefix+" · "+label,key,
                        current.Value,maximum.Value,Math.Clamp(percent,0,100)));
                }
        }
    }

    /// <summary>Mapped vitals first, then every bars panel's gauges in declaration order. The strip hides when both are empty.</summary>
    private void Compose()
    {
        _items.Children.Clear();
        foreach(var card in _mapped) _items.Children.Add(card);
        foreach(var card in _script) _items.Children.Add(card);
        IsVisible=_items.Children.Count>0;
    }

    private void SyncScripts()
    {
        var panels=_panels?.Panels.Where(panel=>panel.IsBars && panel.IsVisible).ToArray() ?? [];
        // Every widget of a shown bars panel is watched, so a value update or a kind change reaches the strip
        // without the panel itself changing.
        var widgets=panels.SelectMany(panel=>panel.Widgets).ToArray();
        foreach(var (widget,handler) in _watched.Where(entry=>!widgets.Contains(entry.Key)).ToArray())
        {
            widget.Changed-=handler;
            _watched.Remove(widget);
            _cards.Remove(widget);
        }
        var ordered=new List<ResourceBar>();
        foreach(var widget in widgets)
        {
            if(!_watched.ContainsKey(widget))
            {
                Action<bool> handler=rebuilt=>{ if(rebuilt) SyncScripts(); else Fill(widget); };
                widget.Changed+=handler;
                _watched[widget]=handler;
            }
            // A label on a bars panel is accepted but has no card; only gauges join the strip.
            if(widget.Kind!="gauge") { _cards.Remove(widget); continue; }
            if(!_cards.TryGetValue(widget,out var card)) _cards[widget]=card=new ResourceBar();
            Fill(widget);
            ordered.Add(card);
        }
        if(_script.SequenceEqual(ordered)) { IsVisible=_items.Children.Count>0; return; }
        _script.Clear();
        _script.AddRange(ordered);
        Compose();
    }

    private void Fill(ScriptPanelWidget widget)
    {
        if(!_cards.TryGetValue(widget,out var card)) return;
        var properties=widget.Properties;
        var warned=properties.Warn is { } warn && properties.Maximum>0 && properties.Number/properties.Maximum<=warn;
        card.Update(properties.Label ?? widget.Id,ScriptPanelAction.Measure(properties.Number,properties.Maximum),
            properties.Maximum>0?properties.Number/properties.Maximum*100:0,warned?ResourceBar.ColorFor("health"):null,this);
    }

    private void Recolor()
    {
        foreach(var widget in _cards.Keys) Fill(widget);
    }

    private static Control Card(Item item)
    {
        var card=new ResourceBar();
        card.Update(item.Label,item.Current.ToString("0.##",Wandur.Core.Localization.UiLanguage.Culture)+" / "+item.Maximum.ToString("0.##",Wandur.Core.Localization.UiLanguage.Culture),
            item.Percentage,ResourceBar.ColorFor(item.Key));
        return card;
    }
    private static int Priority(string key)=>key switch {"health"=>0,"mana" or "spell_points"=>1,"movement" or "endurance"=>2,"energy" or "psionic_points"=>3,"shield"=>4,"hull"=>5,_=>10};
    private static string Label(string key,string fallback)=>key switch
    {
        "health"=>L.VitalsHealth,"mana"=>L.VitalsMana,"movement"=>L.VitalsMovement,"energy"=>L.VitalsEnergy,
        "ammunition"=>L.VitalsAmmunition,"shield"=>L.VitalsShield,"hull"=>L.VitalsHull,"fuel"=>L.VitalsFuel,
        "experience" or "xp"=>L.VitalsExperience,_=>fallback
    };
}

/// <summary>The one resource bar card. Script panels render their gauges with it, so a gauge looks
/// and recolors exactly like a mapped vital.</summary>
internal sealed class ResourceBar : Border
{
    private readonly TextBlock _label=new() {FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock _values=new() {FontSize=11,VerticalAlignment=VerticalAlignment.Center};
    private readonly ProgressBar _bar=new() {Minimum=0,Maximum=100,Height=6,MinHeight=0,IsIndeterminate=false};
    private string? _color="";

    public ResourceBar()
    {
        _values.Classes.Add("muted");
        var heading=new Grid {ColumnDefinitions=new ColumnDefinitions("*,Auto"),ColumnSpacing=8,Children={_label,_values}};
        Grid.SetColumn(_values,1);
        _bar.Bind(BackgroundProperty,new DynamicResourceExtension("LineBrush"));
        // Flush to the strip edges so the card fill meets the play column (no floating inset).
        Margin=new Thickness(0); Padding=new Thickness(10,6); CornerRadius=new CornerRadius(0);
        Child=new StackPanel {Spacing=5,Children={heading,_bar}};
        this.Bind(BackgroundProperty,new DynamicResourceExtension("PanelBrush"));
    }

    /// <summary>The caption block, for tests that check how a coded label rendered.</summary>
    internal TextBlock Label=>_label;

    public static string? ColorFor(string key)=>key switch
    {
        "health"=>"#CE6474","mana" or "spell_points"=>"#6399D1","movement" or "endurance" or "energy" or "fuel"=>"#BD9B54",
        "psionic_points"=>"#AB7AC9",
        "shield"=>"#63B5BA","hull"=>"#95A4BE","experience" or "xp"=>"#6CAD8E",_=>null
    };

    /// <param name="palette">The control that binds the terminal palette; when given, color codes in the label render through it.</param>
    public void Update(string label,string values,double percentage,string? color,Control? palette=null)
    {
        if(palette is null) _label.Text=label; else MudText.Apply(_label,label,palette);
        var plain=MudColorCodes.Strip(label);
        _values.Text=values; _bar.Value=Math.Clamp(percentage,0,100);
        AutomationProperties.SetName(_bar,plain);
        ToolTip.SetTip(this,plain+": "+values);
        if(_color==color) return;
        _color=color;
        // A named color is a literal brush; everything else follows the theme's accent.
        if(color is null) _bar.Bind(RangeBase.ForegroundProperty,new DynamicResourceExtension("AccentBrush"));
        else _bar.Foreground=Brush.Parse(color);
    }
}
