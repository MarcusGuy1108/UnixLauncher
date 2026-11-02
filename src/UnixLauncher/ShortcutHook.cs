using System;
using System.Runtime.InteropServices;
using System.Windows;
using UnixLauncher.Views;

namespace UnixLauncher;

/// <summary>
/// Takes over Windows-key shortcuts the user chose to replace (Settings › Windows Integration).
/// Explorer owns Win+E / Win+S / Win+A / Win+N, so RegisterHotKey can't have them; a low-level keyboard hook can.
/// The decisions live in WinKeyFilter (unit-tested); this is just the Win32 wrapper.
/// </summary>
static class ShortcutHook
{
    static IntPtr _hook;
    static Native.LowLevelProc? _proc; // keep the delegate alive while Windows holds a pointer to it
    static readonly WinKeyFilter Filter = new();

    public static void Apply()
    {
        var s = Settings.Current;
        Filter.Combos.Clear();
        if (s.ReplaceExplorer) Filter.Combos[0x45] = "finder";               // Win+E
        if (s.ReplaceSearch) Filter.Combos[0x53] = Filter.Combos[0x51] = "spotlight"; // Win+S, Win+Q
        if (s.ReplaceQuickSettings) Filter.Combos[0x41] = "controlcentre";   // Win+A
        if (s.ReplaceNotifications) Filter.Combos[0x4E] = "notifications";   // Win+N
        Filter.ReplaceStart = s.ReplaceStart;

        bool want = Filter.ReplaceStart || Filter.Combos.Count > 0;
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
            int msg = w.ToInt32();
            bool down = msg is 0x100 or 0x104, up = msg is 0x101 or 0x105; // WM_(SYS)KEYDOWN / UP
            if ((k.flags & 0x10) == 0 && (down || up)) // skip injected keys, including our own mask key
            {
                var r = Filter.Handle((int)k.vkCode, down);
                if (r.InjectMask)
                {
                    Native.keybd_event(0xE8, 0, 0, IntPtr.Zero);   // an unassigned key: Windows then won't treat the
                    Native.keybd_event(0xE8, 0, 0x2, IntPtr.Zero); // Win release as "open Start"
                }
                // Act after returning: a hook must answer fast or Windows drops it.
                if (r.Action is { } action) Application.Current.Dispatcher.BeginInvoke(() => Run(action));
                if (r.Swallow) return new IntPtr(1);
            }
        }
        return Native.CallNextHookEx(_hook, code, w, l);
    }

    static void Run(string action)
    {
        switch (action)
        {
            case "finder": FinderWindow.Open(); break;
            case "spotlight": App.Spotlight.Toggle(); break;
            case "controlcentre": App.ControlCentre.Toggle(); break;
            case "notifications": App.NotificationCentre.Toggle(); break;
            case "launchpad": App.Launchpad.Toggle(); break;
        }
    }
}
