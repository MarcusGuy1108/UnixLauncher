using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace UnixLauncher;

/// <summary>
/// "UnixLauncher.exe --uninstall" (also what Windows Settings › Apps › Uninstall runs).
/// Puts Windows back exactly as it was, then removes the program's own files.
/// </summary>
static class Uninstaller
{
    public static void Run(bool quiet)
    {
        if (!quiet && MessageBox.Show(
                "Uninstall UnixLauncher?\n\nEverything it changed is put back: File Explorer opens folders, This PC and the Recycle Bin again, " +
                "Explorer is your shell and taskbar again, the Windows key and shortcuts go back to Windows, and it stops starting at sign-in.",
                "Uninstall UnixLauncher", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        StopRunningCopy();
        var failed = Sys.RevertSystemChanges();
        EnsureExplorer();
        Sys.ShowTaskbar(); // again now Explorer is surely up: restores the user's own auto-hide setting

        if (!quiet && MessageBox.Show("Also delete your UnixLauncher settings, custom icons and logs?\n\n" + Settings.Dir,
                "Uninstall UnixLauncher", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            try { Directory.Delete(Settings.Dir, recursive: true); }
            catch (Exception ex) { failed.Add($"Settings folder: {ex.Message}"); }
        }

        RemoveProgramFiles();
        if (!quiet)
            MessageBox.Show(failed.Count == 0
                    ? "UnixLauncher has been removed and Windows is back to normal."
                    : "UnixLauncher has been removed, but these couldn't be undone:\n\n" + string.Join("\n", failed),
                "Uninstall UnixLauncher");
    }

    /// <summary>Ask the running launcher to quit (it restores the taskbar and screen space on exit) and wait for it.</summary>
    static void StopRunningCopy()
    {
        IntPtr bar = Native.FindWindow(null, App.BarTitle);
        if (bar == IntPtr.Zero) return;
        var data = new Native.COPYDATASTRUCT { dwData = new IntPtr(App.QuitMessage) };
        Native.SendCopyData(bar, Native.WM_COPYDATA, IntPtr.Zero, ref data);
        using var running = new Mutex(false, App.InstanceMutex);
        try { if (running.WaitOne(TimeSpan.FromSeconds(10))) running.ReleaseMutex(); }
        catch (AbandonedMutexException) { /* it exited without releasing: that's still "gone" */ }
    }

    /// <summary>If UnixLauncher was the shell, there's no desktop or taskbar now: start Explorer.</summary>
    static void EnsureExplorer()
    {
        if (Native.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero) return;
        Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        for (int i = 0; i < 40 && Native.FindWindow("Shell_TrayWnd", null) == IntPtr.Zero; i++) Thread.Sleep(250);
    }

    /// <summary>
    /// Delete our own files once this process has exited: only files with our names (plus the README we ship in icons\);
    /// folders go only if that leaves them empty, so nothing else stored there is touched.
    /// </summary>
    static void RemoveProgramFiles()
    {
        string dir = AppContext.BaseDirectory.TrimEnd('\\');
        var ours = new[] { "UnixLauncher.exe", "UnixLauncher.dll", "UnixLauncher.pdb", "UnixLauncher.deps.json", "UnixLauncher.runtimeconfig.json" }
            .Select(f => $"\"{Path.Combine(dir, f)}\"");
        string icons = Path.Combine(dir, "icons");
        string cmd = $"/c timeout /t 3 /nobreak >nul & del /f /q {string.Join(" ", ours)} \"{Path.Combine(icons, "README.md")}\" & rmdir \"{icons}\" & rmdir \"{dir}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false });
    }
}
