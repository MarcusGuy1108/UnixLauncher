using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace UnixLauncher;

/// <summary>Launching, power, taskbar, shell registration, startup apps, Recycle Bin.</summary>
static class Sys
{
    public static string ExePath => Environment.ProcessPath!;

    // ---------- Launching ----------
    public static void Launch(string target, string args = "")
    {
        if (target == "::launchpad") { App.Launchpad.Toggle(); return; }
        if (target == "::finder") { Views.FinderWindow.Open(); return; }
        try
        {
            var psi = new ProcessStartInfo(target, args) { UseShellExecute = true };
            if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target)) psi.WorkingDirectory = Path.GetDirectoryName(target)!;
            Process.Start(psi);
        }
        catch (Exception) when (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start("explorer.exe", target); // older builds only resolve shell: paths through Explorer
        }
        catch (Exception ex) { MessageBox.Show($"Couldn't open {target}\n\n{ex.Message}", "UnixLauncher"); }
    }

    public static void ShowInExplorer(string path) => Process.Start("explorer.exe", $"/select,\"{path}\"");
    public static void ShowInFinder(string path) => Views.FinderWindow.Reveal(path);

    // ---------- Power ----------
    static bool Confirm(string what) =>
        MessageBox.Show($"Are you sure you want to {what} now?", "UnixLauncher", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    static void Shutdown(string args) => Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false });
    public static void Sleep() => Native.SetSuspendState(false, false, false);
    public static void Restart() { if (Confirm("restart your computer")) Shutdown("/r /t 0"); }
    public static void PowerOff() { if (Confirm("shut down your computer")) Shutdown("/s /t 0"); }
    public static void LogOut() { if (Confirm("log out")) Shutdown("/l"); }
    public static void Lock() => Native.LockWorkStation();

    // ---------- Windows taskbar (on-top mode) ----------
    static readonly string TaskbarStateFile = Path.Combine(Settings.Dir, "taskbar-state.txt");
    public static bool TaskbarHidden { get; private set; }

    static IEnumerable<IntPtr> TrayWindows()
    {
        var main = Native.FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) yield return main;
        for (var h = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null); h != IntPtr.Zero;
             h = Native.FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null))
            yield return h;
    }

    static Native.APPBARDATA TaskbarData() =>
        new() { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = Native.FindWindow("Shell_TrayWnd", null) };

    public static void HideTaskbar()
    {
        var abd = TaskbarData();
        if (abd.hWnd == IntPtr.Zero) return;
        // Remember the user's own auto-hide setting on disk so a crash can't lose it.
        if (!File.Exists(TaskbarStateFile))
        {
            Directory.CreateDirectory(Settings.Dir);
            File.WriteAllText(TaskbarStateFile, Native.SHAppBarMessage(Native.ABM_GETSTATE, ref abd).ToInt64().ToString());
        }
        abd.lParam = new IntPtr(Native.ABS_AUTOHIDE); // frees the screen space the taskbar reserves
        Native.SHAppBarMessage(Native.ABM_SETSTATE, ref abd);
        foreach (var h in TrayWindows()) Native.ShowWindow(h, Native.SW_HIDE);
        TaskbarHidden = true;
    }

    /// <summary>Explorer sometimes re-shows the taskbar (e.g. after the Win key); hide it again.</summary>
    public static void ReassertTaskbar()
    {
        if (!TaskbarHidden) return;
        foreach (var h in TrayWindows()) if (Native.IsWindowVisible(h)) Native.ShowWindow(h, Native.SW_HIDE);
    }

    public static void ShowTaskbar()
    {
        var abd = TaskbarData();
        if (abd.hWnd == IntPtr.Zero) return;
        if (File.Exists(TaskbarStateFile) && long.TryParse(File.ReadAllText(TaskbarStateFile), out long state))
        {
            abd.lParam = new IntPtr(state);
            Native.SHAppBarMessage(Native.ABM_SETSTATE, ref abd);
            File.Delete(TaskbarStateFile);
        }
        foreach (var h in TrayWindows()) Native.ShowWindow(h, Native.SW_SHOW);
        TaskbarHidden = false;
    }

    // ---------- Shell registration ----------
    const string WinlogonKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsRegisteredShell
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(WinlogonKey);
            return (k?.GetValue("Shell") as string)?.Contains(ExePath, StringComparison.OrdinalIgnoreCase) == true;
        }
    }

    /// <summary>Per-user shell override (HKCU). Takes effect at next sign-in.</summary>
    public static void SetRegisteredShell(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(WinlogonKey);
        if (on) k.SetValue("Shell", $"\"{ExePath}\"");
        else k.DeleteValue("Shell", false);
    }

    public static bool StartsWithWindows
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("UnixLauncher") != null; }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("UnixLauncher", $"\"{ExePath}\""); else k.DeleteValue("UnixLauncher", false);
        }
    }

    // ---------- Startup apps (Explorer normally does this; in shell mode we must) ----------
    public static void RunStartupAppsOnce()
    {
        // Volatile key: Windows deletes it at sign-out, so a launcher restart won't relaunch everything.
        using (var marker = Registry.CurrentUser.OpenSubKey(@"Software\UnixLauncher\Session"))
            if (marker != null) return;
        Registry.CurrentUser.CreateSubKey(@"Software\UnixLauncher\Session", true, RegistryOptions.Volatile)?.Dispose();

        var keys = new (RegistryKey root, string path)[]
        {
            (Registry.CurrentUser, RunKey),
            (Registry.LocalMachine, RunKey),
            (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
        };
        foreach (var (root, path) in keys)
        {
            using var k = root.OpenSubKey(path);
            if (k == null) continue;
            foreach (var name in k.GetValueNames())
            {
                if (name == "UnixLauncher" || IsStartupDisabled(root, name)) continue;
                if (k.GetValue(name) is not string cmd) continue;
                try
                {
                    var (file, args) = CommandLine.Split(Environment.ExpandEnvironmentVariables(cmd), File.Exists);
                    Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
                }
                catch (Exception ex) { App.Log($"Startup item {name}: {ex.Message}"); }
            }
        }
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir))
                if (!file.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
                    catch (Exception ex) { App.Log($"Startup item {file}: {ex.Message}"); }
        }
    }

    /// <summary>Respect items the user switched off in Task Manager › Startup apps.</summary>
    static bool IsStartupDisabled(RegistryKey root, string name)
    {
        using var k = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
        return k?.GetValue(name) is byte[] b && b.Length > 0 && (b[0] & 1) == 1;
    }

    // ---------- Recycle Bin ----------
    [StructLayout(LayoutKind.Sequential)]
    struct SHQUERYRBINFO { public int cbSize; public long i64Size, i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);

    public static bool TrashHasItems()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 && info.i64NumItems > 0;
    }

    public static void EmptyTrash() => Native.SHEmptyRecycleBin(IntPtr.Zero, null, 0); // Windows shows its own confirmation
}
