using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using System.Runtime.InteropServices;

namespace Wandur.Desktop.Services;

// Coordinates in this model are measured from the native parent's top left.
internal sealed class MacTrafficLightPosition
{
    private Point? _last;
    private Point _baseline;

    internal Point Resolve(Rect frame, Rect bounds, bool flipped, bool inset)
    {
        var current = new Point(frame.X - bounds.X, flipped ? frame.Y - bounds.Y : bounds.Bottom - frame.Bottom);
        // AppKit may reset either axis independently. Compare in top-left coordinates so
        // autoresizing a parent does not turn our previous offset into a new baseline.
        _baseline = new Point(_last?.X == current.X ? _baseline.X : current.X,
            _last?.Y == current.Y ? _baseline.Y : current.Y);
        var down = bounds.Height - _baseline.Y - frame.Height >= 4 ? 4 : 3;
        var target = inset ? _baseline + new Vector(4, down) : _baseline;
        if (!new Rect(bounds.Size).Contains(new Rect(target, frame.Size))) target = _baseline;
        _last = target;
        return new Point(bounds.X + target.X, flipped ? bounds.Y + target.Y : bounds.Bottom - target.Y - frame.Height);
    }
}

/// <summary>Moves the existing AppKit buttons; never changes native/Avalonia titlebar sizing.</summary>
internal sealed class MacTrafficLightInset : IDisposable
{
    private readonly Window _window;
    private readonly DispatcherTimer _settle;
    private readonly (nint Button, nint Parent, MacTrafficLightPosition Position)[] _buttons = new (nint, nint, MacTrafficLightPosition)[3];
    private bool _pending;
    private bool _disposed;
    private int _remaining;

    internal MacTrafficLightInset(Window window)
    {
        _window = window;
        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture is not (Architecture.Arm64 or Architecture.X64))
            return;
        _settle.Tick += OnSettle;
        window.Opened += OnRequest;
        window.Activated += OnRequest;
        window.SizeChanged += OnSizeChanged;
        window.PropertyChanged += OnPropertyChanged;
        window.Closed += OnClosed;
        ThemeService.Applied += Request;
        if (window.IsVisible) Request();
    }

    private void OnRequest(object? sender, EventArgs e) => Request();
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => Request();
    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == Window.ExtendClientAreaTitleBarHeightHintProperty)
            Request();
    }

    private void Request()
    {
        if (_disposed || !_window.IsVisible) return;
        _remaining = 5;
        _settle.Start();
        if (_pending) return;
        _pending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pending = false;
            if (!_disposed) Apply();
        }, DispatcherPriority.Background);
    }

    private void OnSettle(object? sender, EventArgs e)
    {
        if (_disposed) return;
        // A short, bounded tail catches AppKit's deferred layout/state transitions.
        // There is no LayoutUpdated subscription, native height write or idle polling.
        if (--_remaining <= 0) _settle.Stop();
        Apply();
    }

    private void Apply(bool restore = false)
    {
        // Avalonia 12.1.2 TopLevelImpl.cs: MacOSTopLevelHandle(IAvnWindowBase)
        // exposes ObtainNSWindowHandle() with the descriptor "NSWindow" (not NSView).
        if (_window.TryGetPlatformHandle() is not { HandleDescriptor: "NSWindow", Handle: var handle } || handle == 0)
            return;
        var inset = !restore && _window.WindowState is not (WindowState.FullScreen or WindowState.Minimized)
            && (Native.Send(handle, Native.StyleMask) & (1L << 14)) == 0
            && !Native.Boolean(handle, Native.IsMiniaturized);
        for (var i = 0; i < _buttons.Length; i++)
        {
            var button = Native.Button(handle, Native.StandardWindowButton, i);
            if (button == 0) continue;
            var parent = Native.Send(button, Native.Superview);
            if (parent == 0) continue;
            ref var state = ref _buttons[i];
            if (state.Button != button || state.Parent != parent)
                state = (button, parent, new MacTrafficLightPosition());
            var frame = Native.Rect(button, Native.Frame);
            var bounds = Native.Rect(parent, Native.Bounds);
            var origin = state.Position.Resolve(frame, bounds, Native.Boolean(parent, Native.IsFlipped), inset);
            if (origin != frame.Position)
                Native.SetOrigin(button, Native.SetFrameOrigin, new NativePoint(origin.X, origin.Y));
        }
    }

    private void OnClosed(object? sender, EventArgs e) => Detach();

    public void Dispose()
    {
        if (_disposed) return;
        if (OperatingSystem.IsMacOS() && _window.IsVisible) Apply(restore: true);
        Detach();
    }

    private void Detach()
    {
        _disposed = true;
        _settle.Stop();
        _settle.Tick -= OnSettle;
        _window.Opened -= OnRequest;
        _window.Activated -= OnRequest;
        _window.SizeChanged -= OnSizeChanged;
        _window.PropertyChanged -= OnPropertyChanged;
        _window.Closed -= OnClosed;
        ThemeService.Applied -= Request;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(double X, double Y);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(double X, double Y, double Width, double Height);

    private static class Native
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        internal static readonly nint StandardWindowButton = Selector("standardWindowButton:"),
            Superview = Selector("superview"), Frame = Selector("frame"), Bounds = Selector("bounds"),
            IsFlipped = Selector("isFlipped"), SetFrameOrigin = Selector("setFrameOrigin:"),
            StyleMask = Selector("styleMask"), IsMiniaturized = Selector("isMiniaturized");

        [DllImport(ObjC, EntryPoint = "sel_registerName")]
        private static extern nint Selector(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        internal static extern nint Send(nint receiver, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool Boolean(nint receiver, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        internal static extern nint Button(nint receiver, nint selector, nint kind);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        internal static extern void SetOrigin(nint receiver, nint selector, NativePoint point);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern NativeRect RectArm64(nint receiver, nint selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")]
        private static extern void RectX64(out NativeRect result, nint receiver, nint selector);

        internal static Rect Rect(nint receiver, nint selector)
        {
            // CGRect is four CGFloat doubles: arm64 returns it in registers, x64 uses stret.
            NativeRect result;
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64) RectX64(out result, receiver, selector);
            else result = RectArm64(receiver, selector);
            return new Rect(result.X, result.Y, result.Width, result.Height);
        }
    }
}
