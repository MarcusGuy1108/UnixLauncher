using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
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
    // Apps that draw their own minimise/maximise/close (browsers, Settings, Discord) can't lose them natively:
    // a cover in the title bar's colour goes exactly over them, where UI Automation says they are.
    readonly Window _cover;
    readonly System.Windows.Controls.Border _coverFill = new();
    (IntPtr hwnd, int w, int h) _coverKey;
    (int fromRight, int fromTop, int w, int h)? _coverAt; // relative to the window's top-right, so it follows moves
    IntPtr _retryFor;
    int _retries;

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
        _cover.MouseLeftButtonDown += (_, e) => // it hides the buttons, never presses them: acts as plain title bar
        {
            IntPtr h = _coverKey.hwnd;
            if (e.ClickCount == 2) { Native.SysCommand(h, Native.IsZoomed(h) ? Native.SC_RESTORE : Native.SC_MAXIMIZE); return; }
            Native.ReleaseCapture();
            Native.PostMessage(h, Native.WM_SYSCOMMAND, new IntPtr(0xF012), IntPtr.Zero);
        };
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
        Settings.Changed += () => { NativeButtons.RestoreAll(); _coverKey = default; Track(Native.GetForegroundWindow()); }; // re-applied as needed
    }

    void Track(IntPtr fg)
    {
        TrackLights(fg);
        PlaceCover(fg);
    }

    /// <summary>Follow the focused window, or the background window whose corner the pointer is on.</summary>
    void TrackLights(IntPtr fg)
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

    /// <summary>Cover the focused app's own caption buttons when it draws them itself.</summary>
    void PlaceCover(IntPtr fg)
    {
        if (!Settings.Current.HideCaptionButtons || !Eligible(fg)) { _cover.Hide(); return; }
        var r = Native.FrameBounds(fg);
        var key = (fg, r.Right - r.Left, r.Bottom - r.Top);
        if (key != _coverKey) // new window or size: find its buttons again (off the UI thread; UI Automation can be slow)
        {
            _coverKey = key;
            _coverAt = null;
            if (fg != _retryFor) { _retryFor = fg; _retries = 0; }
            _cover.Hide(); // out of the way, so asking "what's at this point" finds the app, not us
            double s = UI.Scale(this);
            Task.Run(() => FindButtons(fg, r, s)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                if (_coverKey != key) return;
                if (t.Result is not { } found)
                {
                    // Chromium only builds its accessibility tree once something asks: look again a few times.
                    if (_retries++ < 3) Task.Delay(800).ContinueWith(_ => Dispatcher.BeginInvoke(() => { if (_coverKey == key) _coverKey = default; }));
                    return;
                }
                _coverAt = found.at;
                _coverFill.Background = new SolidColorBrush(found.colour);
                PlaceCover(Native.GetForegroundWindow());
            }));
            return;
        }
        if (_coverAt is not { } a) { _cover.Hide(); return; }
        var b = new Native.RECT { Left = r.Right - a.fromRight, Top = r.Top + a.fromTop };
        b.Right = b.Left + a.w; b.Bottom = b.Top + a.h;
        if (Covered(fg, b)) { _cover.Hide(); return; }
        if (!_cover.IsVisible) _cover.Show();
        Native.SetWindowPos(UI.Hwnd(_cover), Native.HWND_TOPMOST, b.Left, b.Top, a.w, a.h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Where are the app's own minimise/maximise/close? Asks UI Automation what is at the close button's spot, then takes
    /// the run of buttons beside it. Chromium browsers that don't answer get their standard layout measured from the screen.
    /// Nothing found (classic apps, whose buttons are already gone natively): no cover.
    /// </summary>
    static ((int, int, int, int) at, Color colour)? FindButtons(IntPtr h, Native.RECT r, double s)
    {
        // Only something that is plainly the top-right caption buttons.
        bool Plausible(Rect b) => !b.IsEmpty && b.Width <= 4 * 60 * s && b.Height >= 16 * s && b.Height <= 64 * s
                                  && r.Right - b.Right <= 16 * s && b.Top - r.Top <= 16 * s;
        Rect all;
        try { all = FromAutomation(h, r, s) ?? Rect.Empty; }
        catch (Exception) { all = Rect.Empty; } // the app went away or doesn't answer UI Automation
        if (!Plausible(all) && IsChromiumBrowser(h)) all = ChromiumButtons(r, s);
        if (!Plausible(all)) return null;
        // The title bar's colour: the most common colour in a strip just left of the buttons (an accent line or an icon can't win).
        int y = (int)(all.Top + all.Height / 2);
        if (MostCommon((int)(all.Left - 48 * s), (int)(all.Left - 4 * s), y) is not { } colour) return null;
        return (((int)(r.Right - all.Left), (int)(all.Top - r.Top), (int)Math.Ceiling(all.Width), (int)Math.Ceiling(all.Height)), colour);
    }

    static Rect? FromAutomation(IntPtr h, Native.RECT r, double s)
    {
        var close = AutomationElement.FromPoint(new Point(r.Right - 20 * s, r.Top + 12 * s));
        Native.GetWindowThreadProcessId(h, out uint pid);
        if (close == null || close.Current.ProcessId != pid || close.Current.ControlType != ControlType.Button) return null;
        var parent = TreeWalker.ControlViewWalker.GetParent(close);
        if (parent == null) return null;
        var buttons = parent.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().Select(e => e.Current.BoundingRectangle).Where(b => !b.IsEmpty)
            .OrderByDescending(b => b.Left).ToList();
        var all = close.Current.BoundingRectangle;
        foreach (var b in buttons) // the buttons touching the close button, leftwards (not e.g. a tab-search button further off)
            if (b.Right >= all.Left - 2 * s && b.Right <= all.Left + 2 * s && Math.Abs(b.Top - all.Top) <= 4 * s) all.Union(b);
        return all;
    }

    // shortcut: browsers only, whose caption buttons are Windows-sized (45 px each); add other Chromium apps when their sizes are known.
    static bool IsChromiumBrowser(IntPtr h)
    {
        if (!IsChromium(h)) return false;
        Native.GetWindowThreadProcessId(h, out uint pid);
        string exe = Path.GetFileNameWithoutExtension(Native.GetProcessPath(pid) ?? "").ToLowerInvariant();
        return exe is "brave" or "chrome" or "msedge" or "vivaldi" or "opera" or "chromium" or "thorium";
    }

    /// <summary>Chromium's three 45 px buttons at the top-right; their height is where the tab strip meets the toolbar.</summary>
    static Rect ChromiumButtons(Native.RECT r, double s)
    {
        int x = r.Right - (int)(3 * s), top = r.Top + (int)(2 * s), max = (int)(64 * s); // the close button's right margin: no glyph
        var column = Pixels(x, top, 1, max);
        if (column == null) return Rect.Empty;
        int height = Array.FindIndex(column, c => Diff(c, column[0]) > 12);
        if (height < 0) return Rect.Empty;
        double w = 3 * 45 * s;
        return new Rect(r.Right - w, r.Top, w, height + (top - r.Top));
    }

    static Color? MostCommon(int x0, int x1, int y)
    {
        var row = Pixels(x0, y, Math.Max(1, x1 - x0), 1);
        if (row == null) return null;
        uint c = row.GroupBy(p => p).OrderByDescending(g => g.Count()).First().Key;
        return Color.FromRgb((byte)c, (byte)(c >> 8), (byte)(c >> 16)); // COLORREF is 0x00BBGGRR
    }

    /// <summary>Screen pixels (row by row), leaving out layered windows, so our own cover and buttons are never read.</summary>
    static uint[]? Pixels(int x, int y, int w, int h)
    {
        IntPtr screen = Native.GetDC(IntPtr.Zero), mem = Native.CreateCompatibleDC(screen), bmp = Native.CreateCompatibleBitmap(screen, w, h);
        IntPtr old = Native.SelectObject(mem, bmp);
        try
        {
            if (!Native.BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020)) return null; // SRCCOPY
            var px = new uint[w * h];
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                    px[j * w + i] = Native.GetPixel(mem, i, j);
            return px;
        }
        finally
        {
            Native.SelectObject(mem, old);
            Native.DeleteObject(bmp);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
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
