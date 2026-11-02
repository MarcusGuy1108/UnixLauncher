using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace UnixLauncher.Views;

/// <summary>
/// macOS window buttons for other apps: round red/yellow/green buttons on the top-left of the focused window, or of any
/// window whose corner you point at. A separate topmost window: nothing is injected into other apps.
/// Windows' own buttons are hidden by <see cref="NativeButtons"/>.
/// shortcut: assumes a ~32px title bar.
/// </summary>
public partial class TrafficLightOverlay : Window
{
    const double CaptionHeight = 32;
    IntPtr _hwnd, _target, _hoverWin;
    readonly DispatcherTimer _resample = new() { Interval = TimeSpan.FromMilliseconds(300) };
    // Windows opening over the title bar (flyouts, dialogs, other apps' popups) send us no event, so look again now and then.
    readonly DispatcherTimer _recheck = new() { Interval = TimeSpan.FromMilliseconds(250) };
    (IntPtr hwnd, int w, int h) _sampledFor;
    // Pointer over the buttons' corner: show the pill. Polled, because hidden buttons let clicks through and so get no mouse events.
    readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(40) };
    DateTime _lastOver;
    bool _busy;      // the app draws something in that corner (a tab, its icon): buttons only on hover
    bool _revealed;  // the pill is showing
    bool _clickThrough;

    public TrafficLightOverlay()
    {
        InitializeComponent();
        _hwnd = UI.Hwnd(this);
        Native.SetToolWindow(_hwnd, true);
        // Title bars change just after focus changes (and after their buttons go); look at the corner again once settled.
        _resample.Tick += (_, _) => { _resample.Stop(); _sampledFor = default; Track(Native.GetForegroundWindow()); };

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
        Settings.Changed += () => { NativeButtons.RestoreAll(); Track(Native.GetForegroundWindow()); }; // re-applied below as needed
    }

    /// <summary>Follow the focused window, or the background window whose corner the pointer is on.</summary>
    void Track(IntPtr fg)
    {
        IntPtr w = _hoverWin != IntPtr.Zero && Native.IsWindow(_hoverWin) ? _hoverWin : fg;
        if (!Eligible(w)) { _target = IntPtr.Zero; Hide(); return; }
        if (Settings.Current.HideCaptionButtons) NativeButtons.Hide(w);
        if (w != _target) _resample.Start();
        _target = w;
        long style = Native.Style(w);
        MinBtn.IsEnabled = (style & Native.WS_MINIMIZEBOX) != 0; // grey, as macOS shows buttons a window doesn't have
        ZoomBtn.IsEnabled = (style & Native.WS_MAXIMIZEBOX) != 0;

        var r = Native.FrameBounds(w);
        double s = UI.Scale(this);
        int x = r.Left + (int)(2 * s), y = r.Top + (int)((CaptionHeight - Height) / 2 * s); // dots start 12px in, as on macOS
        var area = new Native.RECT { Left = x, Top = y, Right = x + (int)(Width * s), Bottom = y + (int)(Height * s) };
        if (_sampledFor != (w, r.Right - r.Left, r.Bottom - r.Top)) // new window or new size (maximise/restore)
        {
            _sampledFor = (w, r.Right - r.Left, r.Bottom - r.Top);
            // If the app draws something in that corner (a tab, its icon, its title), the buttons wait for the pointer and
            // come with the pill; if the corner is empty (Discord), they stay. Painting over it never matched (Mica, tabs).
            _busy = CornerBusy(area, s);
            ApplyReveal(animate: false);
        }
        // Never draw over something that is on top of the app (a flyout, a dialog, another window): the buttons belong under it.
        if (Covered(w, area)) { Hide(); return; }
        if (!IsVisible) Show();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    void HoverTick()
    {
        Native.GetCursorPos(out var p);
        double sc = UI.Scale(this);
        DateTime now = DateTime.Now;

        // macOS shows a background window's buttons when you point at them: move to the window under the pointer.
        IntPtr under = Native.GetAncestor(Native.WindowFromPoint(p), 2), fg = Native.GetForegroundWindow(); // GA_ROOT
        bool inCorner = false;
        if (under != IntPtr.Zero && !RunningApps.IsOwn(under))
        {
            var fr = Native.FrameBounds(under);
            inCorner = p.X >= fr.Left && p.X < fr.Left + 90 * sc && p.Y >= fr.Top && p.Y < fr.Top + CaptionHeight * sc;
            if (inCorner && under != _target && under != fg && Eligible(under))
            {
                _hoverWin = under;
                _lastOver = now;
                Track(fg);
            }
        }

        bool over = inCorner && under == _target;
        if (IsVisible && Native.GetWindowRect(_hwnd, out var r))
        {
            int pad = (int)(6 * sc);
            over |= p.X >= r.Left - pad && p.X < r.Right + pad && p.Y >= r.Top - pad && p.Y < r.Bottom + pad;
        }
        if (over) _lastOver = now;
        bool want = over || (now - _lastOver).TotalMilliseconds < 350; // a moment's grace, so it doesn't flicker at the edge
        if (!want && _hoverWin != IntPtr.Zero) // left a background window's corner: back to the focused window
        {
            _hoverWin = IntPtr.Zero;
            _revealed = false;
            Track(fg);
            return;
        }
        if (!IsVisible || want == _revealed) return;
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

    void Close_Click(object sender, RoutedEventArgs e) => Native.PostMessage(_target, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); // works without a system menu too
    void Min_Click(object sender, RoutedEventArgs e) => Native.SysCommand(_target, Native.SC_MINIMIZE);
    void Zoom_Click(object sender, RoutedEventArgs e) =>
        Native.SysCommand(_target, Native.IsZoomed(_target) ? Native.SC_RESTORE : Native.SC_MAXIMIZE);
}

/// <summary>
/// Hides Windows' own minimise / maximise / close the way Windows itself would: a window without a system menu
/// (WS_SYSMENU) gets no caption buttons and no title bar icon, drawn natively, so there is nothing to line up or colour-match.
/// The minimise/maximise abilities stay (snapping, Win+↑, double-click to zoom still work). Put back on quit, when the
/// setting changes, and on the next start if the launcher didn't exit cleanly.
/// Apps that draw their own buttons (browsers, Discord, Office, Terminal) aren't touched: it would change nothing there.
/// </summary>
static class NativeButtons
{
    static readonly HashSet<IntPtr> _hidden = new();
    static string StateFile => Path.Combine(Settings.Dir, "hidden-window-buttons.txt");

    public static void Hide(IntPtr h)
    {
        long style = Native.Style(h);
        if ((style & Native.WS_SYSMENU) == 0 || (style & Native.WS_CAPTION) != Native.WS_CAPTION) return; // nothing to hide
        if (Native.GetClass(h).StartsWith("Chrome_WidgetWin_")) return; // draws its own buttons
        if (_hidden.Add(h)) Save();
        SetStyle(h, style & ~Native.WS_SYSMENU);
    }

    public static void RestoreAll()
    {
        foreach (var h in _hidden) Restore(h);
        _hidden.Clear();
        Save();
    }

    /// <summary>After a crash: give the windows we changed their buttons back.</summary>
    public static void RestoreLeftovers()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            foreach (var line in File.ReadAllLines(StateFile))
                if (long.TryParse(line, out long v)) Restore(new IntPtr(v));
            File.Delete(StateFile);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    static void Restore(IntPtr h)
    {
        if (!Native.IsWindow(h)) return;
        long style = Native.Style(h);
        if ((style & Native.WS_CAPTION) == Native.WS_CAPTION && (style & Native.WS_SYSMENU) == 0) SetStyle(h, style | Native.WS_SYSMENU);
    }

    static void SetStyle(IntPtr h, long style)
    {
        if (Native.Style(h) == style) return;
        Native.SetWindowLongPtr(h, Native.GWL_STYLE, new IntPtr(style));
        // Redraw the frame without moving, resizing, reordering or activating it.
        Native.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, 0x20 | Native.SWP_NOMOVE | Native.SWP_NOSIZE | 0x4 | Native.SWP_NOACTIVATE); // FRAMECHANGED, NOZORDER
    }

    static void Save()
    {
        try
        {
            if (_hidden.Count == 0) File.Delete(StateFile);
            else File.WriteAllLines(StateFile, _hidden.Select(h => h.ToInt64().ToString()));
        }
        catch (Exception ex) { App.Log(ex); }
    }
}
