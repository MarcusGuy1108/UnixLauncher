using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace UnixLauncher;

public class WinInfo
{
    public IntPtr Hwnd { get; init; }
    public string Title { get; init; } = "";
    public string? Exe { get; init; }
    public string? Aumid { get; init; }
    public string Key => AppMatch.Key(Exe, Aumid);
}

/// <summary>The same set of windows Alt+Tab shows, in z-order (most recent first).</summary>
static class RunningApps
{
    static readonly uint OwnPid = (uint)Environment.ProcessId;
    static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    public static List<WinInfo> Enumerate()
    {
        var list = new List<WinInfo>();
        Native.EnumWindows((h, _) => { if (IsAppWindow(h)) list.Add(Describe(h)); return true; }, IntPtr.Zero);
        return list;
    }

    public static bool IsOwn(IntPtr h) { Native.GetWindowThreadProcessId(h, out uint pid); return pid == OwnPid; }

    public static bool IsAppWindow(IntPtr h) =>
        Native.IsWindowVisible(h)
        && Native.GetWindow(h, 4) == IntPtr.Zero // GW_OWNER: owned popups/dialogs belong to their owner
        && (Native.ExStyle(h) & Native.WS_EX_TOOLWINDOW) == 0
        && Native.GetText(h).Length > 0
        && !Native.IsCloaked(h) // hidden Store-app frames and other virtual desktops
        && !Native.IsShellWindow(h)
        && !IsOwn(h);

    public static WinInfo Describe(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        return new WinInfo { Hwnd = h, Title = Native.GetText(h), Exe = Native.GetProcessPath(pid), Aumid = Native.GetWindowAumid(h) };
    }

    /// <summary>Human name for an app: Start-menu name for Store apps, else the exe's description.</summary>
    public static string DisplayName(WinInfo w)
    {
        if (w.Aumid != null && AppIndex.ByAumid(w.Aumid) is { } app) return app.Name;
        if (w.Exe == null) return w.Title;
        if (NameCache.TryGetValue(w.Exe, out var cached)) return cached;
        string name;
        try { name = FileVersionInfo.GetVersionInfo(w.Exe).FileDescription ?? ""; } catch { name = ""; }
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(w.Exe);
        return NameCache[w.Exe] = name.Trim();
    }

    public static string IconPath(WinInfo w) => w.Aumid != null && AppIndex.ByAumid(w.Aumid) != null ? @"shell:AppsFolder\" + w.Aumid : w.Exe ?? "";

    public static DockPin ToPin(WinInfo w) => w.Aumid != null && AppIndex.ByAumid(w.Aumid) is { } app
        ? app.ToPin()
        : new DockPin { Name = DisplayName(w), Launch = w.Exe ?? "", Exe = w.Exe };
}

/// <summary>Foreground / move notifications from WinEvent hooks (no polling, no DLL injection).</summary>
static class WindowEvents
{
    /// <summary>Foreground window changed, or a window was minimised/restored.</summary>
    public static event Action<IntPtr>? Foreground;
    /// <summary>The foreground window moved or resized.</summary>
    public static event Action<IntPtr>? Moved;

    static Native.WinEventProc? _proc; // keep the delegate alive: the hook calls it from native code

    public static void Start()
    {
        _proc = OnEvent;
        Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _proc, 0, 0, 0);
        Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _proc, 0, 0, 2); // SKIPOWNPROCESS
    }

    static void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (evt == Native.EVENT_OBJECT_LOCATIONCHANGE)
        {
            if (idObject == 0 && hwnd == Native.GetForegroundWindow()) Moved?.Invoke(hwnd);
        }
        else Foreground?.Invoke(Native.GetForegroundWindow());
    }
}
