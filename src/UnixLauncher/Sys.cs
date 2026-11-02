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
        if (!on && !IsRegisteredShell) return; // never remove a custom shell someone else set up
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

    // ---------- Finder as the folder handler ----------
    // Same mechanism Explorer replacements use: a per-user default verb on folders, drives, This PC and the
    // Recycle Bin, so opening any of them anywhere (desktop, Explorer, "Open folder" in other apps) runs us instead.
    const string FolderVerb = "UnixLauncher.Finder";
    static readonly (string Key, string Arg)[] FolderClasses =
    {
        (@"Software\Classes\Directory\shell", "%1"),
        (@"Software\Classes\Drive\shell", "%1"),
        (@"Software\Classes\CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\shell", Views.FinderWindow.ThisPcPath), // This PC
        (@"Software\Classes\CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shell", Views.FinderWindow.TrashPath),  // Recycle Bin
    };

    public static bool OpensFoldersInFinder
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(FolderClasses[0].Key); return k?.GetValue("") as string == FolderVerb; }
        set
        {
            foreach (var (cls, arg) in FolderClasses)
            {
                if (value)
                {
                    using var shell = Registry.CurrentUser.CreateSubKey(cls);
                    shell.SetValue("", FolderVerb);
                    using var verb = shell.CreateSubKey(FolderVerb);
                    verb.SetValue("", "Open in Finder");
                    using var cmd = verb.CreateSubKey("command");
                    cmd.SetValue("", $"\"{ExePath}\" --open \"{arg}\"");
                }
                else
                {
                    using (var shell = Registry.CurrentUser.OpenSubKey(cls, writable: true))
                    {
                        if (shell == null) continue;
                        if (shell.GetValue("") as string == FolderVerb) shell.DeleteValue("", false); // only undo our own change
                        shell.DeleteSubKeyTree(FolderVerb, false);
                    }
                    DeleteIfEmpty(cls);
                }
            }
        }
    }

    /// <summary>Remove a key we created, and its parents up to Software\Classes, but only while they're empty.</summary>
    static void DeleteIfEmpty(string path)
    {
        for (; path.Length > @"Software\Classes".Length && path.Contains('\\'); path = path[..path.LastIndexOf('\\')])
        {
            using (var k = Registry.CurrentUser.OpenSubKey(path))
                if (k == null || k.SubKeyCount > 0 || k.ValueCount > 0) return;
            Registry.CurrentUser.DeleteSubKey(path, false);
        }
    }

    // ---------- Install / uninstall ----------
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\UnixLauncher";

    /// <summary>List UnixLauncher under Windows Settings › Apps, so it can be uninstalled from there (per user, no admin).</summary>
    public static void RegisterUninstaller()
    {
        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", "UnixLauncher");
        k.SetValue("DisplayIcon", ExePath);
        k.SetValue("Publisher", "UnixLauncher");
        k.SetValue("DisplayVersion", typeof(Sys).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        k.SetValue("InstallLocation", AppContext.BaseDirectory);
        k.SetValue("UninstallString", $"\"{ExePath}\" --uninstall");
        k.SetValue("QuietUninstallString", $"\"{ExePath}\" --uninstall --quiet");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Put back everything UnixLauncher changes outside its own folder: folder / This PC / Recycle Bin handlers,
    /// the shell replacement, start at sign-in, the Windows taskbar (and its auto-hide setting), and our registry keys.
    /// Each step is independent so one failure can't leave the rest in place.
    /// </summary>
    public static List<string> RevertSystemChanges()
    {
        var failed = new List<string>();
        void Step(string what, Action undo)
        {
            try { undo(); }
            catch (Exception ex) { failed.Add($"{what}: {ex.Message}"); App.Log(ex); }
        }
        Step("Folder handler", () => OpensFoldersInFinder = false);
        Step("Windows shell", () => SetRegisteredShell(false));
        Step("Start at sign-in", () => StartsWithWindows = false);
        Step("Windows taskbar", ShowTaskbar);
        Step("Session key", () => Registry.CurrentUser.DeleteSubKeyTree(@"Software\UnixLauncher", false));
        Step("Apps list entry", () => Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false));
        return failed;
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
