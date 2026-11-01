using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace UnixLauncher;

/// <summary>
/// Reserves screen edges (menu bar, dock) so maximised windows don't cover them.
/// With Explorer running we use the AppBar protocol; as the shell there is no AppBar host,
/// so we set the work area directly.
/// shortcut: primary monitor only; per-monitor bars would need one strip per display.
/// </summary>
static class WorkArea
{
    static readonly uint CallbackMsg = Native.RegisterWindowMessage("UnixLauncher.AppBar");
    static readonly Dictionary<IntPtr, (uint edge, int thickness, Native.RECT applied)> Reserved = new();

    static bool ExplorerRunning => Native.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;
    static Native.RECT Screen => new() { Right = Native.GetSystemMetrics(0), Bottom = Native.GetSystemMetrics(1) };

    static Native.APPBARDATA Data(IntPtr hwnd) =>
        new() { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = hwnd, uCallbackMessage = CallbackMsg };

    public static void Reserve(IntPtr hwnd, uint edge, int thicknessPx)
    {
        if (!ExplorerRunning)
        {
            Reserved[hwnd] = (edge, thicknessPx, default);
            ApplyManual();
            return;
        }

        var abd = Data(hwnd);
        if (!Reserved.ContainsKey(hwnd))
        {
            Native.SHAppBarMessage(Native.ABM_NEW, ref abd);
            HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
        }
        abd.uEdge = edge;
        abd.rc = Screen;
        Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref abd);
        switch (edge) // QUERYPOS may move the strip past other bars; restore our thickness
        {
            case Native.ABE_TOP: abd.rc.Bottom = abd.rc.Top + thicknessPx; break;
            case Native.ABE_BOTTOM: abd.rc.Top = abd.rc.Bottom - thicknessPx; break;
            case Native.ABE_LEFT: abd.rc.Right = abd.rc.Left + thicknessPx; break;
            case Native.ABE_RIGHT: abd.rc.Left = abd.rc.Right - thicknessPx; break;
        }
        // Skip no-op updates: two bars re-applying on each other's change notifications would ping-pong.
        if (Reserved.TryGetValue(hwnd, out var prev) && prev.edge == edge && prev.applied.Equals(abd.rc)) return;
        Native.SHAppBarMessage(Native.ABM_SETPOS, ref abd);
        Reserved[hwnd] = (edge, thicknessPx, abd.rc);
    }

    public static void Release(IntPtr hwnd)
    {
        if (!Reserved.Remove(hwnd)) return;
        if (ExplorerRunning)
        {
            var abd = Data(hwnd);
            Native.SHAppBarMessage(Native.ABM_REMOVE, ref abd);
            HwndSource.FromHwnd(hwnd)?.RemoveHook(Hook);
        }
        else ApplyManual();
    }

    public static void ReleaseAll()
    {
        foreach (var h in Reserved.Keys.ToList()) Release(h);
    }

    static void ApplyManual()
    {
        var wa = Screen;
        foreach (var (edge, t, _) in Reserved.Values)
        {
            if (edge == Native.ABE_TOP) wa.Top = Math.Max(wa.Top, t);
            if (edge == Native.ABE_BOTTOM) wa.Bottom = Math.Min(wa.Bottom, Screen.Bottom - t);
            if (edge == Native.ABE_LEFT) wa.Left = Math.Max(wa.Left, t);
            if (edge == Native.ABE_RIGHT) wa.Right = Math.Min(wa.Right, Screen.Right - t);
        }
        Native.SystemParametersInfo(Native.SPI_SETWORKAREA, 0, ref wa, Native.SPIF_SENDCHANGE);
    }

    static IntPtr Hook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == CallbackMsg && w.ToInt32() == Native.ABN_POSCHANGED && Reserved.TryGetValue(hwnd, out var r))
            Reserve(hwnd, r.edge, r.thickness);
        return IntPtr.Zero;
    }
}
