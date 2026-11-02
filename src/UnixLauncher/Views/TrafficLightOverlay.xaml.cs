using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace UnixLauncher.Views;

/// <summary>
/// macOS window buttons for other apps: round red/yellow/green buttons pinned to the top-left of the focused window,
/// and a cover in the title bar's own colour over Windows' minimise/maximise/close on the right. Both are separate
/// topmost windows: nothing is injected into other apps and nothing about them is changed, so there's nothing to undo.
/// shortcut: assumes a ~32px title bar; apps that draw tabs there (browsers) can be listed in OverlayExclude.
/// </summary>
public partial class TrafficLightOverlay : Window
{
    const double CaptionHeight = 32;
    IntPtr _hwnd, _target;
    readonly Window _cover;
    readonly Border _coverFill = new();
    readonly DispatcherTimer _resample = new() { Interval = TimeSpan.FromMilliseconds(300) };
    // Windows opening over the title bar (flyouts, dialogs, other apps' popups) send us no event, so look again now and then.
    readonly DispatcherTimer _recheck = new() { Interval = TimeSpan.FromMilliseconds(250) };
    (IntPtr hwnd, int w, int h) _sampledFor;
    // Pointer over the buttons' corner: show the pill. Polled, because hidden buttons let clicks through and so get no mouse events.
    readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(40) };
    DateTime _lastOver;
    bool _busy;      // the app draws something in that corner (a browser's first tab): buttons only on hover
    bool _revealed;  // the pill is showing
    bool _clickThrough;

    public TrafficLightOverlay()
    {
        InitializeComponent();
        _hwnd = UI.Hwnd(this);
        Native.SetToolWindow(_hwnd, true);

        _cover = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
            ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize, Width = 1, Height = 1, Content = _coverFill,
        };
        Native.SetToolWindow(UI.Hwnd(_cover), true);
        _cover.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { Zoom_Click(this, e); return; }
            // Drag the real window from its covered title bar: the classic "move from here" system command.
            Native.ReleaseCapture();
            Native.PostMessage(_target, Native.WM_SYSCOMMAND, new IntPtr(0xF012), IntPtr.Zero);
        };
        // Title bars fade from inactive to active colour just after focus changes; sample again once it has settled.
        _resample.Tick += (_, _) => { _resample.Stop(); _sampledFor = default; Track(_target); };

        Root.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource != Root && e.OriginalSource != Pill) return; // the buttons handle their own clicks
            if (e.ClickCount == 2) { Zoom_Click(this, e); return; }
            Native.ReleaseCapture();
            Native.PostMessage(_target, Native.WM_SYSCOMMAND, new IntPtr(0xF012), IntPtr.Zero); // drag the window from here too
        };
        _hover.Tick += (_, _) => HoverTick();
        _hover.Start();
        _recheck.Tick += (_, _) => Track(Native.GetForegroundWindow());
        _recheck.Start();
        WindowEvents.Foreground += Track;
        WindowEvents.Moved += Track;
        Settings.Changed += () => Track(Native.GetForegroundWindow());
    }

    void Track(IntPtr fg)
    {
        if (!Eligible(fg)) { _target = IntPtr.Zero; Hide(); _cover.Hide(); return; }
        if (fg != _target) _resample.Start();
        _target = fg;
        long style = Native.Style(fg);
        MinBtn.IsEnabled = (style & Native.WS_MINIMIZEBOX) != 0; // grey, as macOS shows buttons a window doesn't have
        ZoomBtn.IsEnabled = (style & Native.WS_MAXIMIZEBOX) != 0;

        var r = Native.FrameBounds(fg);
        double s = UI.Scale(this);
        int x = r.Left + (int)(2 * s), y = r.Top + (int)((CaptionHeight - Height) / 2 * s); // dots start 12px in, as on macOS
        var buttons = CaptionButtons(fg, style, r, s);
        if (_sampledFor != (fg, buttons.Width, buttons.Height)) // new window or new size (maximise/restore): re-read its title bar colour
        {
            _sampledFor = (fg, buttons.Width, buttons.Height);
            if (SampleTitleBar(buttons) is { } c) _coverFill.Background = new SolidColorBrush(c);
            // If the app draws something in that corner (a tab, its icon, its title), the buttons wait for the pointer and
            // come with the pill; if the corner is empty (Discord), they stay. Painting over it never matched (Mica, tabs).
            _busy = CornerBusy(new Native.RECT { Left = x, Top = y, Right = x + (int)(Width * s), Bottom = y + (int)(Height * s) }, s);
            ApplyReveal(animate: false);
        }
        // Never draw over something that is on top of the app (a flyout, a dialog, another window): the buttons belong under it.
        var lights = new Native.RECT { Left = x, Top = y, Right = x + (int)(Width * s), Bottom = y + (int)(Height * s) };
        if (Covered(fg, lights)) { Hide(); _cover.Hide(); return; }
        if (!IsVisible) Show();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        PlaceCover(style, buttons, r, s);
    }

    void HoverTick()
    {
        if (!IsVisible) return;
        Native.GetCursorPos(out var p);
        Native.GetWindowRect(_hwnd, out var r);
        int pad = (int)(6 * UI.Scale(this));
        bool over = p.X >= r.Left - pad && p.X < r.Right + pad && p.Y >= r.Top - pad && p.Y < r.Bottom + pad;
        if (over) _lastOver = DateTime.Now;
        bool want = over || (DateTime.Now - _lastOver).TotalMilliseconds < 350; // a moment's grace, so it doesn't flicker at the edge
        if (want == _revealed) return;
        _revealed = want;
        ApplyReveal(animate: true);
    }

    void ApplyReveal(bool animate)
    {
        Fade(Pill, _revealed ? 1 : 0, animate);
        Fade(Dots, _busy && !_revealed ? 0 : 1, animate);
        bool through = _busy && !_revealed; // hidden buttons mustn't eat clicks meant for the tab underneath
        if (through == _clickThrough) return;
        _clickThrough = through;
        long ex = Native.ExStyle(_hwnd);
        Native.SetWindowLongPtr(_hwnd, -20, new IntPtr(through ? ex | 0x20 : ex & ~0x20L)); // GWL_EXSTYLE, WS_EX_TRANSPARENT
    }

    static void Fade(UIElement el, double to, bool animate)
    {
        if (!animate || !UI.Motion) { el.BeginAnimation(OpacityProperty, null); el.Opacity = to; return; }
        el.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 120 : 220)));
    }

    /// <summary>
    /// Does the app draw something in the buttons' corner? Copies that corner of the screen once (a plain BitBlt leaves out
    /// layered windows, so our own buttons and pill aren't in the copy) and checks it is one flat colour, as an empty title bar is.
    /// </summary>
    static bool CornerBusy(Native.RECT area, double scale)
    {
        int w = area.Right - area.Left, h = area.Bottom - area.Top;
        IntPtr screen = Native.GetDC(IntPtr.Zero), mem = Native.CreateCompatibleDC(screen), bmp = Native.CreateCompatibleBitmap(screen, w, h);
        IntPtr old = Native.SelectObject(mem, bmp);
        try
        {
            if (!Native.BitBlt(mem, 0, 0, w, h, screen, area.Left, area.Top, 0x00CC0020)) return false; // SRCCOPY; unreadable: assume empty
            // Skip the window's rounded corner (the wallpaper shows there) and the very edges.
            int x0 = (int)(8 * scale), y0 = (int)(5 * scale), x1 = w - (int)(3 * scale), y1 = h - (int)(3 * scale), step = Math.Max(1, (int)scale);
            uint first = Native.GetPixel(mem, x0, y0);
            for (int y = y0; y < y1; y += step)
                for (int x = x0; x < x1; x += step)
                    if (Diff(Native.GetPixel(mem, x, y), first) > 24) return true;
            return false;
        }
        finally
        {
            Native.SelectObject(mem, old);
            Native.DeleteObject(bmp);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    static int Diff(uint a, uint b) =>
        Math.Max(Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)),
        Math.Max(Math.Abs((int)(a >> 8 & 0xFF) - (int)(b >> 8 & 0xFF)), Math.Abs((int)(a >> 16 & 0xFF) - (int)(b >> 16 & 0xFF))));

    /// <summary>Is any window above <paramref name="target"/> overlapping <paramref name="area"/>? Our own and click-through windows don't count.</summary>
    static bool Covered(IntPtr target, Native.RECT area)
    {
        for (var h = Native.GetWindow(target, 3); h != IntPtr.Zero; h = Native.GetWindow(h, 3)) // GW_HWNDPREV: next window up
        {
            if (!Native.IsWindowVisible(h) || Native.IsCloaked(h) || RunningApps.IsOwn(h) || (Native.ExStyle(h) & 0x20) != 0) continue; // WS_EX_TRANSPARENT
            if (Native.GetWindowRect(h, out var r) && r.Left < area.Right && area.Left < r.Right && r.Top < area.Bottom && area.Top < r.Bottom) return true;
        }
        return false;
    }

    void PlaceCover(long style, Native.RECT b, Native.RECT frame, double scale)
    {
        if (!Settings.Current.HideCaptionButtons || (style & Native.WS_SYSMENU) == 0) { _cover.Hide(); return; } // no buttons to hide
        // Chromium/Electron draw their own buttons, at sizes Windows doesn't report: a cover never lines up, so they keep theirs.
        if (IsChromium(_target)) { _cover.Hide(); return; }
        // A cover trailing behind a window being dragged, or one placed from odd button bounds some apps report,
        // lands on top of the app's content: only cover what is plainly the buttons in the top-right corner.
        bool plausible = b.Height >= 16 * scale && b.Height <= 64 * scale && b.Width <= 46 * 4 * scale
                      && Math.Abs(b.Top - frame.Top) <= 12 * scale && Math.Abs(b.Right - frame.Right) <= 16 * scale;
        if (WindowEvents.MovingOrSizing || !plausible) { _cover.Hide(); return; }
        if (!_cover.IsVisible) _cover.Show();
        Native.SetWindowPos(UI.Hwnd(_cover), Native.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    /// <summary>Screen rectangle of the window's minimise/maximise/close buttons.</summary>
    static Native.RECT CaptionButtons(IntPtr h, long style, Native.RECT frame, double scale)
    {
        // DWM knows where it put them (relative to the window rect); apps that draw their own use the same spot.
        if (Native.DwmGetWindowAttribute(h, 5, out Native.RECT rel, Marshal.SizeOf<Native.RECT>()) == 0 && rel.Width > 0 && rel.Height > 0
            && Native.GetWindowRect(h, out var win))
            return new Native.RECT { Left = win.Left + rel.Left, Top = win.Top + rel.Top, Right = win.Left + rel.Right, Bottom = win.Top + rel.Bottom };
        int count = (style & (Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX)) != 0 ? 3 : 1; // fallback: Windows' 46×32 buttons
        return new Native.RECT { Right = frame.Right, Left = frame.Right - (int)(46 * count * scale), Top = frame.Top, Bottom = frame.Top + (int)(32 * scale) };
    }

    /// <summary>The title bar's colour just left of the buttons, read from the screen.</summary>
    static Color? SampleTitleBar(Native.RECT buttons)
    {
        IntPtr dc = Native.GetDC(IntPtr.Zero);
        try
        {
            uint c = Native.GetPixel(dc, buttons.Left - 6, buttons.Top + Math.Max(2, buttons.Height / 4));
            return c == 0xFFFFFFFF ? null : Color.FromRgb((byte)c, (byte)(c >> 8), (byte)(c >> 16)); // COLORREF is 0x00BBGGRR
        }
        finally { Native.ReleaseDC(IntPtr.Zero, dc); }
    }

    static bool IsChromium(IntPtr h) => Native.GetClass(h).StartsWith("Chrome_WidgetWin_");

    static bool Eligible(IntPtr h)
    {
        if (!Settings.Current.TrafficLightOverlay || h == IntPtr.Zero || !Native.IsWindowVisible(h) || Native.IsIconic(h)) return false;
        // Chromium/Electron apps (Brave, Discord…) draw their own title bar, some without WS_CAPTION: a minimise box marks a normal window.
        long style = Native.Style(h);
        bool normal = (style & Native.WS_CAPTION) == Native.WS_CAPTION || (IsChromium(h) && (style & Native.WS_MINIMIZEBOX) != 0);
        if (!normal || (Native.ExStyle(h) & Native.WS_EX_TOOLWINDOW) != 0) return false;
        if (Native.IsShellWindow(h) || Native.IsCloaked(h) || RunningApps.IsOwn(h) || Native.IsFullscreen(h)) return false;
        Native.GetWindowThreadProcessId(h, out uint pid);
        var exe = Path.GetFileName(Native.GetProcessPath(pid) ?? "");
        return !Settings.Current.OverlayExclude.Any(x => string.Equals(x, exe, StringComparison.OrdinalIgnoreCase));
    }

    void Close_Click(object sender, RoutedEventArgs e) => Native.SysCommand(_target, Native.SC_CLOSE);
    void Min_Click(object sender, RoutedEventArgs e) => Native.SysCommand(_target, Native.SC_MINIMIZE);
    void Zoom_Click(object sender, RoutedEventArgs e) =>
        Native.SysCommand(_target, Native.IsZoomed(_target) ? Native.SC_RESTORE : Native.SC_MAXIMIZE);
}
