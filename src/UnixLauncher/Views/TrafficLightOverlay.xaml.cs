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
    (IntPtr hwnd, int w, int h) _sampledFor;

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
        int x = r.Left + (int)(1 * s), y = r.Top + (int)((CaptionHeight - Height) / 2 * s);
        var buttons = CaptionButtons(fg, style, r, s);
        if (_sampledFor != (fg, buttons.Width, buttons.Height)) // new window or new size (maximise/restore): re-read its title bar colour
        {
            _sampledFor = (fg, buttons.Width, buttons.Height);
            if (SampleTitleBar(buttons) is { } c) Backing.Background = _coverFill.Background = new SolidColorBrush(c);
        }
        if (!IsVisible) Show();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        PlaceCover(style, buttons);
    }

    void PlaceCover(long style, Native.RECT b)
    {
        if (!Settings.Current.HideCaptionButtons || (style & Native.WS_SYSMENU) == 0) { _cover.Hide(); return; } // no buttons to hide
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

    static bool Eligible(IntPtr h)
    {
        if (!Settings.Current.TrafficLightOverlay || h == IntPtr.Zero || !Native.IsWindowVisible(h) || Native.IsIconic(h)) return false;
        if ((Native.Style(h) & Native.WS_CAPTION) != Native.WS_CAPTION || (Native.ExStyle(h) & Native.WS_EX_TOOLWINDOW) != 0) return false;
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
