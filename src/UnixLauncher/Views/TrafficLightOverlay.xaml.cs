using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace UnixLauncher.Views;

/// <summary>
/// Floating red/yellow/green buttons pinned to the top-left of whichever app window is focused.
/// It is a separate topmost window, so nothing is injected into other apps; their own
/// minimise/maximise/close buttons stay where they are.
/// shortcut: assumes a ~32px title bar; apps that draw tabs there (browsers) can be listed in OverlayExclude.
/// </summary>
public partial class TrafficLightOverlay : Window
{
    const double CaptionHeight = 32;
    IntPtr _hwnd, _target;

    public TrafficLightOverlay()
    {
        InitializeComponent();
        _hwnd = UI.Hwnd(this);
        Native.SetToolWindow(_hwnd, true);
        WindowEvents.Foreground += Track;
        WindowEvents.Moved += Track;
        Settings.Changed += () => Track(Native.GetForegroundWindow());
    }

    void Track(IntPtr fg)
    {
        if (!Eligible(fg)) { _target = IntPtr.Zero; if (IsVisible) Hide(); return; }
        _target = fg;
        var r = Native.FrameBounds(fg);
        double s = UI.Scale(this);
        int x = r.Left + (int)(12 * s), y = r.Top + (int)((CaptionHeight - Height) / 2 * s);
        if (!IsVisible) Show();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
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
