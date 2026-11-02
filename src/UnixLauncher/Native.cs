using System;
using System.Runtime.InteropServices;
using System.Text;

namespace UnixLauncher;

/// <summary>All Win32 P/Invoke in one place.</summary>
static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const long WS_CAPTION = 0x00C00000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
    public const int SW_HIDE = 0, SW_SHOW = 5, SW_RESTORE = 9;
    public const uint WM_SYSCOMMAND = 0x0112, WM_CLOSE = 0x0010, WM_HOTKEY = 0x0312, WM_SETTINGCHANGE = 0x001A;
    public const int SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_RESTORE = 0xF120, SC_CLOSE = 0xF060;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MINIMIZEEND = 0x0017, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    public const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;
    public const int ABN_POSCHANGED = 1, ABS_AUTOHIDE = 1;
    public const uint SPI_GETWORKAREA = 0x30, SPI_SETWORKAREA = 0x2F, SPIF_SENDCHANGE = 0x2;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr val);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool altTab);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int idx);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] public static extern bool LockWorkStation();
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string msg);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint action, uint param, ref RECT rc, uint winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern int SetWindowCompositionAttribute(IntPtr h, ref WindowCompositionAttribData data);

    [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHEmptyRecycleBin(IntPtr h, string? root, uint flags);
    [DllImport("shell32.dll")] public static extern int SHGetPropertyStoreForWindow(IntPtr h, ref Guid iid, out IPropertyStore store);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid iid, out IShellItemImageFactory factory);

    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
    [DllImport("powrprof.dll")] public static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProc(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetMenu(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] public static extern IntPtr GetSubMenu(IntPtr menu, int pos);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetMenuString(IntPtr menu, uint item, StringBuilder sb, int max, uint flags);
    [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr tpm);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool SHObjectProperties(IntPtr hwnd, uint type, string name, string? page);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);

    /// <summary>Windows' name for a file type ("PDF Document"), from the extension alone (no disk access).</summary>
    public static string TypeName(string fileName)
    {
        var fi = new SHFILEINFO();
        SHGetFileInfo(fileName, 0x80, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x400 | 0x10); // FILE_ATTRIBUTE_NORMAL; SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES
        return fi.szTypeName ?? "";
    }

    [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();

    /// <summary>Dark native (Win32) menus, used for the apps' own mirrored menus. Undocumented, Windows 10 1903+.</summary>
    public static void UseDarkMenus()
    {
        try { SetPreferredAppMode(2); FlushMenuThemes(); } // 2 = ForceDark
        catch { /* older Windows: menus just stay light */ }
    }
    [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PROPVARIANT pv);

    // ---- Blur behind (undocumented but stable since Windows 10) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }
    [StructLayout(LayoutKind.Sequential)]
    public struct WindowCompositionAttribData { public int Attribute; public IntPtr Data; public int SizeOfData; }

    public static void EnableBlur(IntPtr hwnd)
    {
        var accent = new AccentPolicy { AccentState = 3 }; // ACCENT_ENABLE_BLURBEHIND
        int size = Marshal.SizeOf(accent);
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, p, false);
            var data = new WindowCompositionAttribData { Attribute = 19, Data = p, SizeOfData = size }; // WCA_ACCENT_POLICY
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    // ---- Shell icons ----
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    // ---- Window AppUserModelID (groups UWP / Store apps correctly) ----
    [StructLayout(LayoutKind.Sequential)] public struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit)]
    public struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr ptr; [FieldOffset(16)] public IntPtr pad; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint c);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY k);
        [PreserveSig] int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
    }

    public static string? GetWindowAumid(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null) return null;
        try
        {
            var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
            if (store.GetValue(ref key, out var pv) != 0) return null;
            string? s = pv.vt == 31 ? Marshal.PtrToStringUni(pv.ptr) : null; // VT_LPWSTR
            PropVariantClear(ref pv);
            return string.IsNullOrEmpty(s) ? null : s;
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    // ---- Small helpers ----
    public static string GetText(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, sb.Capacity); return sb.ToString(); }
    public static string GetClass(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }
    public static long Style(IntPtr h) => GetWindowLongPtr(h, GWL_STYLE).ToInt64();
    public static long ExStyle(IntPtr h) => GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();

    public static string? GetProcessPath(uint pid)
    {
        IntPtr hp = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (hp == IntPtr.Zero) return null;
        try { var sb = new StringBuilder(1024); int n = sb.Capacity; return QueryFullProcessImageName(hp, 0, sb, ref n) ? sb.ToString() : null; }
        finally { CloseHandle(hp); }
    }

    /// <summary>Visible frame bounds (excludes the invisible resize border on Windows 10/11).</summary>
    public static RECT FrameBounds(IntPtr h)
    {
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0) return r;
        GetWindowRect(h, out r);
        return r;
    }

    public static bool IsCloaked(IntPtr h) => DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int v, sizeof(int)) == 0 && v != 0;

    static readonly string[] ShellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow" };
    public static bool IsShellWindow(IntPtr h) => Array.IndexOf(ShellClasses, GetClass(h)) >= 0;

    public static bool IsFullscreen(IntPtr h)
    {
        if (h == IntPtr.Zero || IsShellWindow(h) || !GetWindowRect(h, out var r)) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(h, 2), ref mi)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    public static void SetToolWindow(IntPtr h, bool noActivate)
    {
        long ex = ExStyle(h) | WS_EX_TOOLWINDOW;
        if (noActivate) ex |= WS_EX_NOACTIVATE;
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex));
    }

    public static void SysCommand(IntPtr h, int cmd) => PostMessage(h, WM_SYSCOMMAND, new IntPtr(cmd), IntPtr.Zero);

    public static void Activate(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        SwitchToThisWindow(h, true);
    }

    /// <summary>Press a key combination, e.g. PressKeys(0x5B) = Win, PressKeys(0x5B, 0x4B) = Win+K.</summary>
    public static void PressKeys(params byte[] vks)
    {
        foreach (var vk in vks) keybd_event(vk, 0, 0, IntPtr.Zero);
        for (int i = vks.Length - 1; i >= 0; i--) keybd_event(vks[i], 0, 0x2, IntPtr.Zero); // KEYEVENTF_KEYUP
    }
}
