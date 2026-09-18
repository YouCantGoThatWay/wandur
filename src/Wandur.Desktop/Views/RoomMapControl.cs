using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Wandur.Core.Mapping;
using Wandur.Desktop.ViewModels;
using L = Wandur.Core.Localization.Strings;

namespace Wandur.Desktop.Views;

/// <summary>North-up room tiles and directed routes for one floor. Room edits require explicit edit mode.</summary>
public sealed class RoomMapControl : Control
{
    public required MapViewModel Model { get; init; }
    private readonly List<(string Id, Rect Bounds)> _hits = [];
    private readonly List<(string Id, Rect Bounds)> _destinationHits = [];
    private string? _dragRoomId;
    private Point? _dragPreview;
    private Point? _pointer;
    private Point _pressedAt;
    private bool _dragged;
    private int _clickCount;
    private string? _lastClickedRoomId;
    private double? _pinchStartZoom;
    private static readonly IBrush Ground = Brush.Parse("#10191F");
    public static readonly StyledProperty<IBrush> CanvasBrushProperty = AvaloniaProperty.Register<RoomMapControl, IBrush>(nameof(CanvasBrush), Brush.Parse("#10191F"));
    public static readonly StyledProperty<IBrush> GridBrushProperty = AvaloniaProperty.Register<RoomMapControl, IBrush>(nameof(GridBrush), Brush.Parse("#1D2B34"));
    public IBrush CanvasBrush { get => GetValue(CanvasBrushProperty); set => SetValue(CanvasBrushProperty, value); }
    public IBrush GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    static RoomMapControl() => AffectsRender<RoomMapControl>(CanvasBrushProperty, GridBrushProperty);
    public RoomMapControl()
    {
        this.Bind(CanvasBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("MapCanvasBrush"));
        this.Bind(GridBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("MapGridBrush"));
        GestureRecognizers.Add(new PinchGestureRecognizer());
        AddHandler(InputElement.PinchEvent, OnPinch);
        AddHandler(InputElement.PinchEndedEvent, (_, e) => { _pinchStartZoom = null; e.Handled = true; });
        AddHandler(InputElement.PointerTouchPadGestureMagnifyEvent, OnMagnify);
    }
    private void OnPinch(object? sender, PinchEventArgs e)
    {
        _pinchStartZoom ??= Model.Zoom;
        Model.ZoomAt(_pinchStartZoom.Value * e.Scale, e.ScaleOrigin); e.Handled = true;
    }
    private void OnMagnify(object? sender, PointerDeltaEventArgs e)
    {
        Model.ZoomAt(Model.Zoom * (1 + e.Delta.Y), e.GetPosition(this)); e.Handled = true;
    }
    private static readonly IBrush Mint = Brush.Parse("#81D9BE");
    private static readonly IBrush ExitGlow = Brush.Parse("#4081D9BE");
    private static readonly IBrush Amber = Brush.Parse("#F2BF6A");
    private static readonly IBrush Gray = Brush.Parse("#8497A3");
    private static readonly IBrush Route = Brush.Parse("#C3D6E0");
    private static readonly IBrush Tile = Brush.Parse("#25343E");
    private static readonly Typeface Font = new("Inter");

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e); Model.PropertyChanged += Changed;
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _pinchStartZoom = null; _lastClickedRoomId = null;
        Model.PropertyChanged -= Changed; base.OnDetachedFromVisualTree(e);
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(CanvasBrush, new Rect(Bounds.Size));
        _hits.Clear(); _destinationHits.Clear();
        var viewport = Model.CreateViewport(Bounds.Width, Bounds.Height);
        var rooms = Model.VisibleRooms;
        var byId = Model.Snapshot.Rooms.ToDictionary(r => r.Id);
        var candidates = Model.Snapshot.CandidateRoomIds.ToHashSet();
        var gridMode = Model.IsGridMode;
        var half = gridMode ? viewport.Scale / 2 : Math.Clamp(viewport.Scale * .16, 4, 26);
        var visibleIds = rooms.Select(r => r.Id).ToHashSet();
        var planned = Model.PlannedRoute?.Steps.Select(l => (l.FromId, l.ToId)).ToHashSet() ?? [];
        var palette = MapEnvironmentPalette.Styles;
        Point Project(MapRoom room) => room.Id == _dragRoomId && _dragPreview is { } preview ? preview : viewport.Project(room.X, room.Y);
        // Integer coordinates are room centers; half coordinates are tile boundaries.
        // Keep the same pitch as the tiles even when zoomed out.
        var origin = viewport.Project(-.5, .5);
        var gridSpacing = viewport.Scale;
        var gridPen = new Pen(GridBrush, 1);
        // Iterate screen space, so large maps and large pans do not increase grid work.
        for (var x = origin.X % gridSpacing; x < Bounds.Width; x += gridSpacing)
            context.DrawLine(gridPen, new Point(x, 0), new Point(x, Bounds.Height));
        for (var y = origin.Y % gridSpacing; y < Bounds.Height; y += gridSpacing)
            context.DrawLine(gridPen, new Point(0, y), new Point(Bounds.Width, y));

        var routes = Model.Snapshot.Links.ToLookup(l => (l.FromId, l.ToId));
        var linkedDirections = Model.Snapshot.Links.ToLookup(l => l.FromId);
        var reserved = rooms.Select(r => new Rect(Project(r).X - half - 2, Project(r).Y - half - 2, half * 2 + 4, half * 2 + 4)).ToList();
        var drawn = new HashSet<(string, string)>();
        foreach (var link in Model.Snapshot.Links)
        {
            if (gridMode && !planned.Contains((link.FromId, link.ToId))) continue;
            if (!byId.TryGetValue(link.FromId, out var from) || !visibleIds.Contains(from.Id)) continue;
            if (!byId.TryGetValue(link.ToId, out var to) || !visibleIds.Contains(to.Id)) continue;
            if (drawn.Contains((to.Id, from.Id)) || !drawn.Add((from.Id, to.Id))) continue;
            var start = Project(from); var end = Project(to);
            var delta = new Vector(end.X - start.X, end.Y - start.Y);
            if (delta.Length < 1) continue;
            var unit = delta / delta.Length;
            var trim = gridMode ? 0 : Math.Min(delta.Length / 3, half / Math.Max(Math.Abs(unit.X), Math.Abs(unit.Y)) + 3);
            start += unit * trim; end -= unit * trim;
            var reverse = routes[(to.Id, from.Id)].FirstOrDefault();
            var onRoute = planned.Contains((from.Id, to.Id)) || planned.Contains((to.Id, from.Id));
            var brush = onRoute ? Amber : link.IsLocked || link.DoorState == MapDoorState.Locked ? Brush.Parse("#DE7D88") : Route;
            if (link.LinePoints.Count > 0 && !gridMode)
            {
                var points = new[] { Project(from) }.Concat(link.LinePoints.Select(p => viewport.Project(p.X, p.Y))).Append(Project(to)).ToArray();
                for (var i = 1; i < points.Length; i++)
                {
                    context.DrawLine(new Pen(brush, onRoute ? 3 : 1.8, link.Confirmed ? null : DashStyle.Dash), points[i-1], points[i]);
                    reserved.Add(new Rect(points[i-1], points[i]).Normalize().Inflate(3));
                }
                var finalVector = new Vector(points[^1].X - points[^2].X, points[^1].Y - points[^2].Y);
                if (finalVector.Length > 1) Arrow(context, points[^1] - finalVector / finalVector.Length * (half + 3), finalVector / finalVector.Length, brush);
                var firstVector = new Vector(points[1].X - points[0].X, points[1].Y - points[0].Y);
                if (reverse is not null && firstVector.Length > 1) Arrow(context, points[0] + firstVector / firstVector.Length * (half + 3), -firstVector / firstVector.Length, brush);
                continue;
            }
            // Each half retains its own evidence when the return route is only inferred.
            var middle = start + (end - start) / 2;
            context.DrawLine(new Pen(brush, 1.8, (reverse?.Confirmed ?? link.Confirmed) ? null : DashStyle.Dash), start, middle);
            context.DrawLine(new Pen(brush, 1.8, link.Confirmed ? null : DashStyle.Dash), middle, end);
            Arrow(context, end, unit, brush);
            if (reverse is not null) Arrow(context, start, -unit, brush);
            void DoorMarker(MapLink? exit, Point point)
            {
                if (exit is null || (exit.DoorState == MapDoorState.None && !exit.IsLocked)) return;
                var side = new Vector(-unit.Y, unit.X);
                var doorBrush = exit.IsLocked || exit.DoorState == MapDoorState.Locked ? Brush.Parse("#DE7D88") : brush;
                context.DrawLine(new Pen(doorBrush, exit.DoorState == MapDoorState.Open && !exit.IsLocked ? 1 : 4), point - side * 5, point + side * 5);
            }
            DoorMarker(link, start + (end-start)*.65);
            DoorMarker(reverse, start + (end-start)*.35);
            reserved.Add(new Rect(new Point(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y)),
                new Size(Math.Max(1, Math.Abs(end.X - start.X)), Math.Max(1, Math.Abs(end.Y - start.Y)))).Inflate(4));
        }

        foreach (var room in rooms)
        {
            var screen = Project(room);
            if (screen.X < -half - 120 || screen.Y < -half - 80 || screen.X > Bounds.Width + half + 120 || screen.Y > Bounds.Height + half + 80) continue;
            var current = Model.Snapshot.CurrentRoomId == room.Id;
            var candidate = candidates.Contains(room.Id);
            var selected = Model.SelectedRoomId == room.Id;
            var accent = current ? Mint : candidate ? Amber : Gray;
            var outgoing = linkedDirections[room.Id].ToArray();
            var bounds = new Rect(screen.X - half, screen.Y - half, half * 2, half * 2);
            var style = MapEnvironmentPalette.Resolve(room, palette);
            var fill = Brush.Parse(style.Color);
            var inferred = room.Provisional || (current && Model.Snapshot.State != MapTrackingState.Confirmed);
            context.DrawRectangle(fill, new Pen(gridMode ? Ground : accent, gridMode ? .5 : 1.3,
                inferred && !gridMode ? DashStyle.Dash : null), bounds, 0, 0);
            if (selected) context.DrawRectangle(null, new Pen(Brushes.White, 2), gridMode ? bounds.Deflate(2) : bounds.Inflate(4));
            if (!string.IsNullOrWhiteSpace(style.Symbol) && half >= 8 && !current)
            {
                var symbol = Text(style.Symbol, Ground, Math.Clamp(half, 10, 18));
                context.DrawText(symbol, new Point(screen.X - symbol.Width / 2, screen.Y - symbol.Height / 2));
            }
            if (room.IsLocked && half >= 8) DrawText(context, "×", new Point(bounds.Right - 10, bounds.Top), Ground, 12);
            if (!string.IsNullOrEmpty(room.Notes) && half >= 8) context.DrawEllipse(Brushes.White, new Pen(Ground, 1), new Point(bounds.Left + 4, bounds.Top + 4), 2, 2);
            DrawExitLights(context, room, outgoing, screen, half);
            if (current)
            {
                var markerRadius = Math.Clamp(half * .55, 4, 9);
                context.DrawEllipse(Ground, new Pen(Brushes.White, 1.5), screen, markerRadius + 2, markerRadius + 2);
                context.DrawEllipse(Model.Snapshot.State == MapTrackingState.Confirmed ? Mint : null,
                    new Pen(Mint, 2), screen, markerRadius, markerRadius);
            }
            else if (candidate) DrawText(context, "?", new Point(screen.X - 4, screen.Y - 8), Amber, 13);
            _hits.Add((room.Id, gridMode ? bounds : bounds.Inflate(4)));

            if (!gridMode && (Model.Zoom >= .65 || selected || current))
            {
                var width = Math.Clamp(viewport.Scale - 10, 55, 130);
                using var label = new TextLayout(room.Name, Font, 10, Route,
                    textAlignment: TextAlignment.Center, textWrapping: TextWrapping.Wrap,
                    textTrimming: TextTrimming.CharacterEllipsis, maxWidth: width, maxLines: 2);
                var positions = new[]
                {
                    new Point(screen.X - width / 2, screen.Y + half + 5),
                    new Point(screen.X + half + 7, screen.Y - label.Height / 2),
                    new Point(screen.X - half - width - 7, screen.Y - label.Height / 2)
                };
                foreach (var point in positions)
                {
                    var labelBounds = new Rect(point, new Size(width, label.Height));
                    if (!new Rect(Bounds.Size).Contains(labelBounds) || reserved.Any(r => r.Intersects(labelBounds))) continue;
                    context.FillRectangle(Ground, labelBounds);
                    label.Draw(context, point);
                    reserved.Add(labelBounds);
                    break;
                }
            }

            // Floor badges are navigation of the map only; they never send movement commands.
            var destinations = outgoing.Where(l => byId.TryGetValue(l.ToId, out var target) && (target.Z != room.Z || target.Area != room.Area))
                .Select(l => byId[l.ToId]).DistinctBy(r => r.Id).OrderBy(r => r.Z).ToArray();
            var badgeY = screen.Y - half - 20;
            foreach (var destination in destinations)
            {
                var floor = destination.Z;
                var caption = destination.Area != room.Area ? "↗ " + (destination.Area ?? L.MapUnassignedArea) : (floor > room.Z ? "↑ " : "↓ ") + floor.ToString("0.##", CultureInfo.CurrentCulture);
                var text = Text(caption, Route, 10);
                var badge = new Rect(screen.X + half + 7, badgeY, text.Width + 8, 17);
                context.DrawRectangle(Tile, new Pen(Gray, 1), badge, 3, 3);
                context.DrawText(text, new Point(badge.X + 4, badge.Y + 1));
                _destinationHits.Add((destination.Id, badge));
                badgeY -= 21;
            }
            // Known vertical exits with no destination remain explicitly unexplored.
            var vertical = room.KnownExits.Where(d => d is "up" or "down")
                .Where(d => !outgoing.Any(l => l.Direction == d && byId.ContainsKey(l.ToId))).ToArray();
            if (!gridMode && vertical.Length > 0)
                DrawText(context, string.Join(" ", vertical.Select(d => d == "up" ? "↑?" : "↓?")),
                    new Point(screen.X + half + 6, screen.Y - 7), accent, 10);
        }
        if (gridMode && Model.PlannedRoute is { } route)
            foreach (var step in route.Steps)
                if (byId.TryGetValue(step.FromId, out var from) && byId.TryGetValue(step.ToId, out var to) && visibleIds.Contains(from.Id) && visibleIds.Contains(to.Id))
                    context.DrawLine(new Pen(Amber, 3, DashStyle.Dash), Project(from), Project(to));
        if (_dragPreview is { } ghost) context.DrawRectangle(null, new Pen(Brushes.White, 2, DashStyle.Dash), new Rect(ghost.X-half,ghost.Y-half,half*2,half*2));
        var north = Text("↑ " + L.MapNorth, Mint, 11);
        context.FillRectangle(Ground, new Rect(8, 8, north.Width + 16, 26), 5);
        context.DrawText(north, new Point(16, 13));
    }

    private static FormattedText Text(string text, IBrush brush, double size) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, size, brush);
    private static void DrawText(DrawingContext context, string text, Point point, IBrush brush, double size) =>
        context.DrawText(Text(text, brush, size), point);
    private static void Arrow(DrawingContext context, Point tip, Vector unit, IBrush brush)
    {
        var side = new Vector(-unit.Y, unit.X);
        context.DrawLine(new Pen(brush, 1.8), tip, tip - unit * 6 + side * 3);
        context.DrawLine(new Pen(brush, 1.8), tip, tip - unit * 6 - side * 3);
    }
    private static Vector Direction(string direction) => direction switch
    {
        "north" => new(0, -1), "south" => new(0, 1), "east" => new(1, 0), "west" => new(-1, 0),
        "northeast" => new(.707, -.707), "northwest" => new(-.707, -.707),
        "southeast" => new(.707, .707), "southwest" => new(-.707, .707), _ => default
    };
    private static void DrawExitLights(DrawingContext context, MapRoom room, MapLink[] outgoing, Point center, double half)
    {
        var radius = Math.Clamp(half * .15, .6, 3.2);
        foreach (var direction in room.KnownExits.Concat(outgoing.Select(l => l.Direction)).Distinct())
        {
            var vector = Direction(direction);
            Point point;
            if (vector.Length > 0)
                point = center + vector / Math.Max(Math.Abs(vector.X), Math.Abs(vector.Y)) * (half - radius * .5);
            else if (direction is "up" or "down")
                point = center + new Vector(direction == "up" ? -half * .55 : half * .55,
                    (direction == "up" ? -1 : 1) * (half - radius * .5));
            else continue;
            var link = outgoing.FirstOrDefault(l => l.Direction == direction);
            var blocked = link?.IsLocked == true || link?.DoorState is MapDoorState.Closed or MapDoorState.Locked;
            if (half >= 6) context.DrawEllipse(ExitGlow, null, point, radius + 2, radius + 2);
            context.DrawEllipse(blocked ? Amber : Mint, new Pen(Ground, .8), point, radius, radius);
            if (direction is "up" or "down" && half >= 12)
            {
                var sign = direction == "up" ? -1 : 1;
                context.DrawLine(new Pen(Ground, 1), point + new Vector(-1.4, -sign * .6), point + new Vector(0, sign * .9));
                context.DrawLine(new Pen(Ground, 1), point + new Vector(0, sign * .9), point + new Vector(1.4, -sign * .6));
            }
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Pointer.Type == PointerType.Touch || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        { _lastClickedRoomId = null; return; }
        _clickCount = e.ClickCount;
        _pointer = _pressedAt = e.GetPosition(this); _dragged = false;
        _dragRoomId = Model.IsEditMode ? _hits.LastOrDefault(h => h.Bounds.Contains(_pressedAt)).Id : null;
        _dragPreview = null;
        e.Pointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pointer is not { } previous)
        {
            var hovered = _hits.LastOrDefault(h => h.Bounds.Contains(e.GetPosition(this))).Id;
            var room = Model.Snapshot.Rooms.FirstOrDefault(r => r.Id == hovered);
            ToolTip.SetTip(this, room is null ? null : $"{room.Name}\n{MapEnvironmentPalette.LabelFor(room.Environment)} · ({room.X:0.##}, {room.Y:0.##}, {room.Z:0.##})" +
                (string.IsNullOrEmpty(room.Notes) ? "" : "\n" + room.Notes));
            return;
        }
        var next = e.GetPosition(this);
        if (!_dragged && new Vector(next.X - _pressedAt.X, next.Y - _pressedAt.Y).Length < 4) return;
        _dragged = true; var delta = next - previous;
        if (_dragRoomId is not null) { _dragPreview = next; InvalidateVisual(); }
        else Model.Pan(delta.X, delta.Y);
        _pointer = next; e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pointer is null) return;
        string? walkTo = null;
        if (!_dragged)
        {
            var click = e.GetPosition(this);
            var destination = _destinationHits.LastOrDefault(h => h.Bounds.Contains(click));
            if (destination.Bounds.Width > 0) { _lastClickedRoomId = null; Model.SelectRoom(destination.Id); }
            else
            {
                Model.SelectedRoomId = _hits.LastOrDefault(h => h.Bounds.Contains(click)).Id;
                if (_clickCount == 2 && _lastClickedRoomId == Model.SelectedRoomId &&
                    _hits.Any(h => h.Id == Model.SelectedRoomId && h.Bounds.Contains(_pressedAt))) walkTo = Model.SelectedRoomId;
                _lastClickedRoomId = Model.SelectedRoomId;
            }
        }
        else _lastClickedRoomId = null;
        if (_dragged && _dragRoomId is { } roomId && _dragPreview is { } position)
        {
            var viewport = Model.CreateViewport(Bounds.Width, Bounds.Height);
            var coordinates = viewport.Unproject(position);
            Model.MoveRoom(roomId, Math.Round(coordinates.X), Math.Round(coordinates.Y));
        }
        _pointer = null; _dragRoomId = null; _dragPreview = null; InvalidateVisual();
        e.Pointer.Capture(null); e.Handled = true;
        if (walkTo is not null) Model.WalkToRoomCommand.Execute(walkTo);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (_pointer is not null) _lastClickedRoomId = null;
        _pointer = null; _dragRoomId = null; _dragPreview = null;
        InvalidateVisual(); base.OnPointerCaptureLost(e);
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Model.ZoomAt(Model.Zoom * Math.Pow(1.12, Math.Clamp(e.Delta.Y, -100, 100)), e.GetPosition(this)); e.Handled = true;
    }
}
