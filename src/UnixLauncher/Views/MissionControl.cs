using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace UnixLauncher.Views;

/// <summary>
/// macOS Mission Control: every open window as a live preview (DWM thumbnails), zoomed out from where it is to a grid over the
/// blurred desktop. Point to see its name; click to bring it forward; Esc or the background to leave.
/// The window is a normal (not see-through) window: DWM only draws live previews onto those.
/// </summary>
sealed class MissionControl : Window
{
    sealed class Item
    {
        public IntPtr Hwnd, Thumb;
        public Native.RECT From, To; // window position and grid cell, in pixels relative to this screen
        public Border Ring = null!;
        public TextBlock Label = null!;
    }

    static MissionControl? _open;
    readonly List<Item> _items = new();
    readonly Canvas _canvas = new();
    readonly Native.RECT _mon;
    readonly double _s;
    Item? _hover;
    bool _closing;

    public static void Toggle()
    {
        if (_open != null) { _open.Leave(null); return; }
        _open = new MissionControl();
        _open.Show();
        UI.BringToFront(_open);
    }

    MissionControl()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "Mission Control";
        Content = _canvas;
        _canvas.Background = Brushes.Transparent; // hit-testable everywhere

        IntPtr me = UI.Hwnd(this);
        int square = 1; // DWMWCP_DONOTROUND: a full-screen layer, not a window
        Native.DwmSetWindowAttribute(me, 33, ref square, sizeof(int));
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(Native.MonitorFromPoint(new Native.POINT(), 1), ref mi); // the main screen
        _mon = mi.rcMonitor;
        _s = UI.Scale(this);
        Left = _mon.Left / _s; Top = _mon.Top / _s; Width = _mon.Width / _s; Height = _mon.Height / _s;
        Background = (Brush?)Backdrop.Brush(_mon, Colors.Black, 0.35) ?? new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));

        var wins = RunningApps.Enumerate().Select(w => w.Hwnd)
            .Concat(FinderWindow.All.Select(UI.Hwnd)) // Finder belongs to us, so the scan leaves it out
            .Where(h => !Native.IsIconic(h) && Native.GetWindowRect(h, out var r) && r.Right > _mon.Left && r.Left < _mon.Right && r.Bottom > _mon.Top && r.Top < _mon.Bottom)
            .Distinct().ToList();
        foreach (var h in wins)
        {
            if (Native.DwmRegisterThumbnail(me, h, out var thumb) != 0) continue;
            Native.GetWindowRect(h, out var r);
            var item = new Item { Hwnd = h, Thumb = thumb, From = Offset(r) };
            item.Ring = new Border
            {
                BorderBrush = (Brush)FindResource("Accent"), BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(10), Visibility = Visibility.Hidden,
            };
            item.Label = new TextBlock
            {
                Text = Native.GetText(h) is { Length: > 0 } t ? t : "Window", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold,
                Visibility = Visibility.Hidden, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.9 },
            };
            _canvas.Children.Add(item.Ring);
            _canvas.Children.Add(item.Label);
            _items.Add(item);
        }
        LayOut();
        SetResourceReference(FontFamilyProperty, "UiFont");

        Loaded += (_, _) => Animate(open: true, then: null);
        MouseMove += (_, e) => SetHover(Hit(e.GetPosition(this)));
        MouseLeftButtonUp += (_, e) => Leave(Hit(e.GetPosition(this)));
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Leave(null); };
        Deactivated += (_, _) => Leave(null);
        Closed += (_, _) =>
        {
            foreach (var i in _items) Native.DwmUnregisterThumbnail(i.Thumb);
            _open = null;
        };
    }

    Native.RECT Offset(Native.RECT r) => new() { Left = r.Left - _mon.Left, Top = r.Top - _mon.Top, Right = r.Right - _mon.Left, Bottom = r.Bottom - _mon.Top };

    /// <summary>A grid of cells under the menu bar and above the dock; each window keeps its shape, never grows past its real size.</summary>
    void LayOut()
    {
        int n = _items.Count;
        if (n == 0) return;
        int cols = (int)Math.Ceiling(Math.Sqrt(n)), rows = (int)Math.Ceiling(n / (double)cols);
        double left = 60 * _s, top = 80 * _s, w = _mon.Width - 2 * left, h = _mon.Height - top - 140 * _s, gap = 28 * _s;
        double cw = w / cols, ch = h / rows;
        for (int i = 0; i < n; i++)
        {
            var it = _items[i];
            int row = i / cols, inRow = Math.Min(cols, n - row * cols);
            double rowLeft = left + (w - inRow * cw) / 2; // a short last row is centred
            double scale = Math.Min(1, Math.Min((cw - gap) / Math.Max(1, it.From.Width), (ch - gap - 24 * _s) / Math.Max(1, it.From.Height)));
            double tw = it.From.Width * scale, th = it.From.Height * scale;
            double x = rowLeft + (i % cols) * cw + (cw - tw) / 2, y = top + row * ch + (ch - th) / 2;
            it.To = new Native.RECT { Left = (int)x, Top = (int)y, Right = (int)(x + tw), Bottom = (int)(y + th) };

            Canvas.SetLeft(it.Ring, x / _s - 5); Canvas.SetTop(it.Ring, y / _s - 5);
            it.Ring.Width = tw / _s + 10; it.Ring.Height = th / _s + 10;
            Canvas.SetLeft(it.Label, x / _s); Canvas.SetTop(it.Label, (y + th) / _s + 8);
            it.Label.Width = tw / _s;
        }
    }

    Item? Hit(Point p)
    {
        double x = p.X * _s, y = p.Y * _s;
        return _items.FirstOrDefault(i => x >= i.To.Left && x < i.To.Right && y >= i.To.Top && y < i.To.Bottom);
    }

    void SetHover(Item? item)
    {
        if (item == _hover || _closing) return;
        if (_hover != null) { _hover.Ring.Visibility = Visibility.Hidden; _hover.Label.Visibility = Visibility.Hidden; }
        _hover = item;
        if (item != null) { item.Ring.Visibility = Visibility.Visible; item.Label.Visibility = Visibility.Visible; }
    }

    /// <summary>Zoom every preview back to its window, then bring <paramref name="pick"/> forward (or just leave).</summary>
    void Leave(Item? pick)
    {
        if (_closing) return;
        _closing = true;
        SetHover(null);
        if (pick != null) Native.Activate(pick.Hwnd); // it comes up under us while the previews fly home
        Animate(open: false, then: () =>
        {
            if (pick != null) Native.Activate(pick.Hwnd);
            Close();
        });
    }

    void Animate(bool open, Action? then)
    {
        if (!UI.Motion) { Place(open ? 1 : 0); then?.Invoke(); return; }
        var clock = Stopwatch.StartNew();
        double ms = open ? 260 : 220;
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / ms);
            double e = open ? 1 - Math.Pow(1 - t, 3) : t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            Place(open ? e : 1 - e);
            if (t < 1) return;
            System.Windows.Media.CompositionTarget.Rendering -= tick;
            then?.Invoke();
        };
        System.Windows.Media.CompositionTarget.Rendering += tick;
    }

    /// <summary>Every preview at <paramref name="k"/> of the way from its window (0) to its grid cell (1).</summary>
    void Place(double k)
    {
        int L(int a, int b) => (int)Math.Round(a + (b - a) * k);
        foreach (var i in _items)
        {
            var props = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE,
                rcDestination = new Native.RECT { Left = L(i.From.Left, i.To.Left), Top = L(i.From.Top, i.To.Top), Right = L(i.From.Right, i.To.Right), Bottom = L(i.From.Bottom, i.To.Bottom) },
                fVisible = 1,
            };
            Native.DwmUpdateThumbnailProperties(i.Thumb, ref props);
        }
    }
}
