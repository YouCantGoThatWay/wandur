using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Wandur.Models;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

/// <summary>Shows observed current/maximum pairs without guessing missing capacity.</summary>
public sealed class ResourceBarsView : UserControl
{
    private sealed record Item(string Id,string Label,string Key,double Current,double Maximum,double Percentage);
    private readonly UniformGrid _items=new() {Columns=1};
    private Item[] _shown=[];
    private string _culture="";

    public ResourceBarsView()
    {
        Name="ResourceBars";
        MaxHeight=144;
        VerticalAlignment=VerticalAlignment.Top;
        IsVisible=false;
        var scroll=new ScrollViewer {Content=_items,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxHeight=136};
        var frame=new Border {Padding=new Thickness(8,4),BorderThickness=new Thickness(0,1,0,0),Child=scroll};
        frame.Bind(Border.BackgroundProperty,new DynamicResourceExtension("ShellBrush"));
        frame.Bind(Border.BorderBrushProperty,new DynamicResourceExtension("LineBrush"));
        Content=frame;
        SizeChanged+=(_,e)=>_items.Columns=Math.Clamp((int)(Math.Max(0,e.NewSize.Width-24)/220),1,4);
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
        IsVisible=items.Count>0;
        var next=items.ToArray();
        var culture=Wandur.Core.Localization.UiLanguage.Culture.Name;
        if(_shown.SequenceEqual(next) && _culture==culture) return;
        _culture=culture;
        _shown=next;
        _items.Children.Clear();
        foreach(var item in next) _items.Children.Add(Card(item));

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

    private static Control Card(Item item)
    {
        var label=new TextBlock {Text=item.Label,FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};
        var values=new TextBlock {Text=item.Current.ToString("0.##",Wandur.Core.Localization.UiLanguage.Culture)+" / "+item.Maximum.ToString("0.##",Wandur.Core.Localization.UiLanguage.Culture),
            FontSize=11,VerticalAlignment=VerticalAlignment.Center};
        values.Classes.Add("muted");
        var heading=new Grid {ColumnDefinitions=new ColumnDefinitions("*,Auto"),ColumnSpacing=8,Children={label,values}};
        Grid.SetColumn(values,1);
        var bar=new ProgressBar {Minimum=0,Maximum=100,Value=item.Percentage,Height=6,MinHeight=0,IsIndeterminate=false};
        bar.Bind(BackgroundProperty,new DynamicResourceExtension("LineBrush"));
        var color=item.Key switch
        {
            "health"=>"#CE6474","mana" or "spell_points"=>"#6399D1","movement" or "endurance" or "energy" or "fuel"=>"#BD9B54",
            "psionic_points"=>"#AB7AC9",
            "shield"=>"#63B5BA","hull"=>"#95A4BE","experience" or "xp"=>"#6CAD8E",_=>null
        };
        if(color is null) bar.Bind(ForegroundProperty,new DynamicResourceExtension("AccentBrush"));
        else bar.Foreground=Brush.Parse(color);
        AutomationProperties.SetName(bar,item.Label);
        var card=new Border {Margin=new Thickness(4),Padding=new Thickness(9,6),CornerRadius=new CornerRadius(5),
            Child=new StackPanel {Spacing=5,Children={heading,bar}}};
        card.Bind(Border.BackgroundProperty,new DynamicResourceExtension("PanelBrush"));
        ToolTip.SetTip(card,item.Label+": "+values.Text);
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
