using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wandur.Core.Mapping;
using Wandur.Desktop.ViewModels;
using Wandur.Desktop.Views;

namespace Wandur.Desktop.Tests;

/// <summary>
/// Six rooms across two floors, three of them naming or describing a "temple": the owner asked to be able
/// to search for keywords and highlight the rooms already seen that match. Nothing here sends to the world.
/// </summary>
public sealed class MapRoomSearchTests
{
    private static MapSnapshot SixRoomsTwoFloors() => new(
        [
            new("alpha", "Temple Alpha", "A dusty temple hall.", null, 0, 0, 0, false),
            new("market", "Market Row", "Stalls line the street.", null, 1, 0, 0, false),
            new("gamma", "Temple Gamma", "Incense fills a quiet temple room.", null, 2, 0, 0, false),
            new("guard", "Guard Post", "A watchful guard stands here.", null, 0, 0, 1, false),
            new("beta", "Temple Beta", "A temple shrine on the upper floor.", null, 1, 0, 1, false),
            new("tower", "Watchtower", "Wind howls around the tower.", null, 2, 0, 1, false),
        ],
        [], [], "alpha", MapTrackingState.Confirmed, RoomDataSource.Text, 0);

    [AvaloniaFact]
    public void TypingHighlightsMatchesAcrossFloorsAndEnterStepsThroughThemWithEscapeClearing()
    {
        var tracker = new RoomMapTracker(SixRoomsTwoFloors());
        var model = new MapViewModel(tracker);
        var view = new MapView(model);
        var window = new Window { Content = view, Width = 700, Height = 500 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, model.SelectedFloor);

            var toggle = view.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "MapSearchToggle");
            toggle.IsChecked = true; Dispatcher.UIThread.RunJobs();
            var box = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "MapSearchBox");
            Assert.True(box.IsVisible);

            box.Focus();
            window.KeyTextInput("t"); Dispatcher.UIThread.RunJobs();
            // A single character does not filter yet.
            Assert.False(model.IsRoomSearchActive);
            Assert.Empty(model.RoomSearchMatchIds);

            window.KeyTextInput("emple"); Dispatcher.UIThread.RunJobs();
            Assert.Equal("temple", model.RoomSearchQuery);
            Assert.True(model.IsRoomSearchActive);
            Assert.Equal(3, model.RoomSearchMatchCount);
            Assert.Equal(["alpha", "beta", "gamma"], model.RoomSearchMatchIds.Order());

            var count = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "MapSearchCount");
            Assert.True(count.IsVisible);
            Assert.Contains("3", count.Text);

            // Matches are named in order: Temple Alpha (floor 0), Temple Beta (floor 1), Temple Gamma (floor 0).
            Assert.Equal(["Temple Alpha", "Temple Beta", "Temple Gamma"], model.RoomSearchMatches.Select(r => r.Name));

            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("alpha", model.SelectedRoomId);
            Assert.Equal(0, model.SelectedFloor);
            Assert.Equal(0, model.CenterX); Assert.Equal(0, model.CenterY);

            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("beta", model.SelectedRoomId);
            Assert.Equal(1, model.SelectedFloor);
            Assert.Equal(1, model.CenterX); Assert.Equal(0, model.CenterY);

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("", model.RoomSearchQuery);
            Assert.False(model.IsRoomSearchActive);
            Assert.Empty(model.RoomSearchMatchIds);
            Assert.Equal("", box.Text ?? "");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void OtherFloorMatchesListInADropdownAndJumpingThereSwitchesFloor()
    {
        var tracker = new RoomMapTracker(SixRoomsTwoFloors());
        var model = new MapViewModel(tracker);
        var view = new MapView(model);
        var window = new Window { Content = view, Width = 700, Height = 500 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            model.IsRoomSearchVisible = true;
            model.RoomSearchQuery = "temple";
            Dispatcher.UIThread.RunJobs();
            // On floor 0, only "beta" (floor 1) is reachable from the dropdown.
            Assert.Equal(["beta"], model.RoomSearchOtherFloorMatches.Select(r => r.Id));
            var panel = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MapSearchOtherFloorPanel");
            Assert.True(panel.IsVisible);
            var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "MapSearchOtherFloor");
            Assert.Equal(1, list.ItemCount);
            model.SelectedRoomSearchMatch = model.RoomSearchOtherFloorMatches[0];
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("beta", model.SelectedRoomId);
            Assert.Equal(1, model.SelectedFloor);
            // Having jumped to floor 1, the two remaining matches on floor 0 are now the "other floor" ones.
            Assert.Equal(["alpha", "gamma"], model.RoomSearchOtherFloorMatches.Select(r => r.Id).Order());
        }
        finally { window.Close(); }
    }
}
