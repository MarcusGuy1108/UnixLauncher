using System;
using System.Windows.Threading;

namespace UnixLauncher;

/// <summary>macOS hot corners on the main screen (Settings › Desktop & Dock). Off while a full-screen game is in front.</summary>
static class HotCorners
{
    static readonly HotCornerTrigger Trigger = new();
    static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(60) };

    public static void Start()
    {
        Timer.Tick += (_, _) => Check();
        Timer.Start();
    }

    static void Check()
    {
        if (Settings.Current.HotCorners.Count == 0 || !Native.GetCursorPos(out var p)) return;
        // shortcut: main screen only, like the dock and menu bar; per-monitor corners would need each monitor's bounds.
        string? corner = Trigger.Update(HotCornerTrigger.At(p.X, p.Y, 0, 0, Native.GetSystemMetrics(0), Native.GetSystemMetrics(1)));
        if (corner == null || !Settings.Current.HotCorners.TryGetValue(corner, out var action)) return;
        IntPtr fg = Native.GetForegroundWindow();
        if (!RunningApps.IsOwn(fg) && Native.IsFullscreen(fg)) return; // never interrupt a game
        Run(action);
    }

    public static void Run(string action)
    {
        switch (action)
        {
            case "Mission Control": Views.MissionControl.Toggle(); break;
            case "Desktop": Native.PressKeys(0x5B, 0x44); break;         // Win+D
            case "Launchpad": App.Launchpad.Toggle(); break;
            case "Notification Centre": App.NotificationCentre.Toggle(); break;
            case "Put Display to Sleep": Native.PostMessage(Views.UI.Hwnd(App.Bar), Native.WM_SYSCOMMAND, new IntPtr(0xF170), new IntPtr(2)); break; // SC_MONITORPOWER: off
            case "Lock Screen": Sys.Lock(); break;
        }
    }
}
