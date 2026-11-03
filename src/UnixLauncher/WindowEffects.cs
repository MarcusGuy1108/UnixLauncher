using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using UnixLauncher.Views;

namespace UnixLauncher;

/// <summary>
/// macOS window animations for every app, without touching them: DWM hands us a live picture of any window (the same one
/// Alt+Tab shows), which we fly between the window and its dock icon. Windows' own minimise animation is switched off while
/// the launcher runs (not saved, so it is back after quitting or signing out).
/// </summary>
static class WindowEffects
{
    const uint SPI_GETANIMATION = 0x48, SPI_SETANIMATION = 0x49;
    static int _savedMinAnimate = -1; // Windows' own setting, while we have it switched off

    public static void Start()
    {
        WindowEvents.Minimizing += OnMinimizing;
        Settings.Changed += Apply;
        Apply();
    }

    static bool On => Settings.Current.MinimizeEffect && UI.Motion;

    static void Apply()
    {
        if (On) { if (_savedMinAnimate < 0) { _savedMinAnimate = GetMinAnimate(); SetMinAnimate(0); } }
        else Stop();
    }

    /// <summary>Give Windows its own minimise animation back (quitting, or the setting switched off).</summary>
    public static void Stop()
    {
        if (_savedMinAnimate < 0) return;
        SetMinAnimate(_savedMinAnimate);
        _savedMinAnimate = -1;
    }

    static int GetMinAnimate()
    {
        var ai = new Native.ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<Native.ANIMATIONINFO>() };
        return Native.SystemParametersInfo(SPI_GETANIMATION, ai.cbSize, ref ai, 0) ? ai.iMinAnimate : 1;
    }

    static void SetMinAnimate(int on)
    {
        var ai = new Native.ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<Native.ANIMATIONINFO>(), iMinAnimate = on };
        Native.SystemParametersInfo(SPI_SETANIMATION, ai.cbSize, ref ai, Native.SPIF_SENDCHANGE); // no SPIF_UPDATEINIFILE: never saved
    }

    static void OnMinimizing(IntPtr h)
    {
        if (!On || RunningApps.IsOwn(h) || !RunningApps.IsAppWindow(h) || App.Dock.IconRect(h) is not { } icon) return;
        if (NormalRect(h) is { } from) Fly(h, from, icon, toIcon: true, done: null);
    }

    /// <summary>A minimised window grows out of its dock icon, then is shown for real (macOS restore from the Dock).</summary>
    public static void Restore(IntPtr h, Native.RECT icon)
    {
        if (!On || NormalRect(h) is not { } to) { Native.Activate(h); return; }
        Fly(h, icon, to, toIcon: false, done: () => Native.Activate(h));
    }

    /// <summary>Where the window is when not minimised, in screen pixels (its placement is kept in work-area coordinates).</summary>
    static Native.RECT? NormalRect(IntPtr h)
    {
        var wp = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetWindowPlacement(h, ref wp) || !Native.GetMonitorInfo(Native.MonitorFromWindow(h, 2), ref mi)) return null;
        if ((wp.flags & 2) != 0) return mi.rcWork; // WPF_RESTORETOMAXIMIZED: it fills the screen
        // shortcut: offsets by the window's own monitor; fine for the usual single screen, check on multi-monitor setups.
        int dx = mi.rcWork.Left - mi.rcMonitor.Left, dy = mi.rcWork.Top - mi.rcMonitor.Top;
        var r = wp.rcNormal;
        return new Native.RECT { Left = r.Left + dx, Top = r.Top + dy, Right = r.Right + dx, Bottom = r.Bottom + dy };
    }

    /// <summary>
    /// Move the window's live picture from one rectangle to another (macOS "Scale" effect): it keeps its shape and ends fitted
    /// inside the icon. Drawn on a surface-less, click-through tool window that exists only for the animation.
    /// </summary>
    static void Fly(IntPtr src, Native.RECT from, Native.RECT to, bool toIcon, Action? done)
    {
        // Fit the window's shape inside the icon, centred.
        var icon = toIcon ? to : from;
        double aspect = (double)Math.Max(1, (toIcon ? from : to).Width) / Math.Max(1, (toIcon ? from : to).Height);
        int fw = aspect >= 1 ? icon.Width : (int)(icon.Height * aspect), fh = aspect >= 1 ? (int)(icon.Width / aspect) : icon.Height;
        var fitted = new Native.RECT { Left = icon.Left + (icon.Width - fw) / 2, Top = icon.Top + (icon.Height - fh) / 2 };
        fitted.Right = fitted.Left + fw; fitted.Bottom = fitted.Top + fh;
        if (toIcon) to = fitted; else from = fitted;

        var area = new Native.RECT
        {
            Left = Math.Min(from.Left, to.Left), Top = Math.Min(from.Top, to.Top),
            Right = Math.Max(from.Right, to.Right), Bottom = Math.Max(from.Bottom, to.Bottom),
        };
        var host = new HwndSource(new HwndSourceParameters("Window animation")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            // NOREDIRECTIONBITMAP (no surface of its own: only the picture shows), TOOLWINDOW, TOPMOST, TRANSPARENT, NOACTIVATE
            ExtendedWindowStyle = 0x00200000 | 0x80 | 0x8 | 0x20 | 0x08000000,
            PositionX = area.Left, PositionY = area.Top, Width = area.Width, Height = area.Height,
        });
        host.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool handled) =>
        {
            if (msg != 0x84) return IntPtr.Zero; // WM_NCHITTEST: never in the way of clicks
            handled = true;
            return new IntPtr(-1); // HTTRANSPARENT
        });
        Native.ShowWindow(host.Handle, 4); // SW_SHOWNOACTIVATE
        if (Native.DwmRegisterThumbnail(host.Handle, src, out IntPtr thumb) != 0) { host.Dispose(); done?.Invoke(); return; }

        var clock = Stopwatch.StartNew();
        const double ms = 300;
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / ms);
            double e = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; // ease in-out, as macOS
            int L(int a, int b) => (int)Math.Round(a + (b - a) * e);
            var props = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY,
                rcDestination = new Native.RECT
                {
                    Left = L(from.Left, to.Left) - area.Left, Top = L(from.Top, to.Top) - area.Top,
                    Right = L(from.Right, to.Right) - area.Left, Bottom = L(from.Bottom, to.Bottom) - area.Top,
                },
                fVisible = 1,
                opacity = (byte)(toIcon ? 255 - 80 * e : 175 + 80 * e), // a touch see-through near the icon
            };
            Native.DwmUpdateThumbnailProperties(thumb, ref props);
            if (t < 1) return;
            CompositionTarget.Rendering -= tick;
            done?.Invoke(); // show the real window before the picture goes, so nothing flickers
            Native.DwmUnregisterThumbnail(thumb);
            host.Dispose();
        };
        CompositionTarget.Rendering += tick;
    }
}
