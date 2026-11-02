using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using UnixLauncher.Views;

namespace UnixLauncher;

/// <summary>
/// Keyboard behaviour the user chose: Windows-key shortcuts taken over (Settings › Windows Integration) and Alt working
/// like the Mac's ⌘ (Settings › Keyboard). Explorer owns Win+E / Win+S / Win+A / Win+N, so RegisterHotKey can't have
/// them; a low-level keyboard hook can. The decisions live in WinKeyFilter and MacKeyFilter (unit-tested); this is the
/// Win32 wrapper. Everything steps aside while a full-screen game or an excluded app is in front.
/// </summary>
static class ShortcutHook
{
    static IntPtr _hook;
    static Native.LowLevelProc? _proc; // keep the delegate alive while Windows holds a pointer to it
    static readonly WinKeyFilter Win = new();
    static readonly MacKeyFilter Mac = new();

    public static void Apply()
    {
        var s = Settings.Current;
        Win.Combos.Clear();
        if (s.ReplaceExplorer) Win.Combos[0x45] = "finder";               // Win+E
        if (s.ReplaceSearch) Win.Combos[0x53] = Win.Combos[0x51] = "spotlight"; // Win+S, Win+Q
        if (s.ReplaceQuickSettings) Win.Combos[0x41] = "controlcentre";   // Win+A
        if (s.ReplaceNotifications) Win.Combos[0x4E] = "notifications";   // Win+N
        Win.ReplaceStart = s.ReplaceStart;

        bool want = Win.ReplaceStart || Win.Combos.Count > 0 || s.MacShortcuts;
        if (want && _hook == IntPtr.Zero)
        {
            _proc = Proc;
            _hook = Native.SetWindowsHookEx(13, _proc, Native.GetModuleHandle(null), 0); // WH_KEYBOARD_LL
            if (_hook == IntPtr.Zero) App.Log($"Keyboard hook failed: {Marshal.GetLastWin32Error()}");
        }
        else if (!want && _hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    static IntPtr Proc(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(l);
            int msg = w.ToInt32(), vk = (int)k.vkCode;
            bool down = msg is 0x100 or 0x104, up = msg is 0x101 or 0x105; // WM_(SYS)KEYDOWN / UP
            if ((k.flags & 0x10) == 0 && (down || up)) // skip injected keys, including our own
            {
                if (down && vk is WinKeyFilter.LWin or WinKeyFilter.RWin or MacKeyFilter.LAlt) DecideWhoseKeysTheseAre();
                var r = Win.Handle(vk, down);
                var m = Mac.Handle(vk, down);
                if (r.InjectMask) // an unassigned key: Windows then won't treat the Win release as "open Start"
                {
                    Inject(new MacKeyFilter.Key(0xE8, false));
                    Inject(new MacKeyFilter.Key(0xE8, true));
                }
                if (m.Send != null) foreach (var key in m.Send) Inject(key);
                // Act after returning: a hook must answer fast or Windows drops it.
                foreach (var action in new[] { r.Action, m.Action })
                    if (action != null) Application.Current.Dispatcher.BeginInvoke(() => Run(action));
                if (r.Swallow || m.Swallow) return new IntPtr(1);
            }
        }
        return Native.CallNextHookEx(_hook, code, w, l);
    }

    /// <summary>Full-screen games and excluded apps keep every key; Finder reads ⌘ (Alt) shortcuts itself.</summary>
    static void DecideWhoseKeysTheseAre()
    {
        IntPtr fg = Native.GetForegroundWindow();
        bool own = RunningApps.IsOwn(fg);
        bool stepAside = (!own && Native.IsFullscreen(fg)) || IsExcluded(fg);
        Win.Enabled = !stepAside;
        Mac.Enabled = Settings.Current.MacShortcuts && !stepAside && !(own && HwndSource.FromHwnd(fg)?.RootVisual is FinderWindow);
    }

    public static bool IsExcluded(IntPtr h)
    {
        if (Settings.Current.OverlayExclude.Count == 0) return false;
        Native.GetWindowThreadProcessId(h, out uint pid);
        var exe = Path.GetFileName(Native.GetProcessPath(pid) ?? "");
        return Settings.Current.OverlayExclude.Any(x => string.Equals(x, exe, StringComparison.OrdinalIgnoreCase));
    }

    static void Inject(MacKeyFilter.Key key)
    {
        // Arrows, Home/End, Win and Print Screen are "extended" keys; without the flag Home can arrive as numpad 7.
        bool extended = key.Vk is >= 0x21 and <= 0x28 or 0x2C or 0x2D or 0x2E or 0x5B or 0x5C;
        uint flags = (key.Up ? 0x2u : 0) | (extended ? 0x1u : 0);
        Native.keybd_event((byte)key.Vk, (byte)Native.MapVirtualKey((uint)key.Vk, 0), flags, IntPtr.Zero);
    }

    static void Run(string action)
    {
        IntPtr fg = Native.GetForegroundWindow();
        switch (action)
        {
            case "finder": FinderWindow.Open(); break;
            case "spotlight": App.Spotlight.Toggle(); break;
            case "controlcentre": App.ControlCentre.Toggle(); break;
            case "notifications": App.NotificationCentre.Toggle(); break;
            case "launchpad": App.Launchpad.Toggle(); break;
            case "minimise" when HwndSource.FromHwnd(fg)?.RootVisual is MacWindow mac: mac.Minimise(); break;
            case "minimise": Native.SysCommand(fg, Native.SC_MINIMIZE); break;
            case "quit" or "hide" when RunningApps.IsOwn(fg):
                if (HwndSource.FromHwnd(fg)?.RootVisual is Window own)
                {
                    if (action == "quit") own.Close();
                    else if (own is MacWindow mac) mac.Minimise();
                    else own.WindowState = WindowState.Minimized;
                }
                break;
            case "quit" or "hide" or "next-window" when !Native.IsShellWindow(fg): // not on the desktop: that's Explorer itself
                var key = RunningApps.Describe(fg).Key;
                if (action == "quit") MenuBar.QuitApp(key);
                else if (action == "hide") MenuBar.HideApp(key);
                else if (RunningApps.Enumerate().Where(x => x.Key == key).ToList() is { Count: > 1 } wins)
                    Native.Activate(wins[^1].Hwnd); // ⌘`: the app's back-most window comes forward
                break;
        }
    }
}
