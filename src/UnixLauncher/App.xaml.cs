using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using UnixLauncher.Views;

namespace UnixLauncher;

public partial class App : Application
{
    Mutex? _single;
    bool _primary; // only the instance that owns the mutex may touch the taskbar on exit

    /// <summary>True when we were started as the Windows shell (no Explorer taskbar running).</summary>
    public static bool ShellMode { get; private set; }

    public static MenuBar Bar { get; private set; } = null!;
    public static DockWindow Dock { get; private set; } = null!;
    public static ControlCentre ControlCentre { get; private set; } = null!;
    public static NotificationCentre NotificationCentre { get; private set; } = null!;
    public static Spotlight Spotlight { get; private set; } = null!;
    public static Launchpad Launchpad { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // "--open <folder>" comes from the folder handler (Settings › Windows Integration).
        int at = Array.IndexOf(e.Args, "--open");
        string? openFolder = at >= 0 && at + 1 < e.Args.Length ? e.Args[at + 1] : null;

        _single = new Mutex(true, "UnixLauncher.SingleInstance", out _primary);
        if (!_primary)
        {
            if (openFolder != null) SendToRunningCopy(openFolder);
            Shutdown();
            return;
        }

        // A shell must not die on one bad handler: log and keep going.
        DispatcherUnhandledException += (_, ex) => { Log(ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => { Log(ex.ExceptionObject); Sys.ShowTaskbar(); };

        Settings.Load();
        Native.UseDarkMenus();
        Resources["UiFont"] = new FontFamily(Settings.Current.FontFamily);

        ShellMode = Native.FindWindow("Shell_TrayWnd", null) == IntPtr.Zero;
        if (ShellMode) Sys.RunStartupAppsOnce();
        else if (Settings.Current.HideWindowsTaskbar) Sys.HideTaskbar();

        WindowEvents.Start();
        AppIndex.LoadAsync();

        Bar = new MenuBar();
        Dock = new DockWindow();
        ControlCentre = new ControlCentre();
        NotificationCentre = new NotificationCentre();
        Spotlight = new Spotlight();
        Launchpad = new Launchpad();
        _ = new TrafficLightOverlay();
        Bar.Show();
        Dock.Show();

        Notifications.Start();
        Notifications.Arrived += n =>
        {
            // Explorer shows its own toasts; we only need banners when we *are* the shell.
            if (ShellMode && !Settings.Current.DoNotDisturb) Banner.Show(n);
        };

        ShortcutHook.Apply();
        if (Settings.Current.ReplaceExplorer) Sys.OpensFoldersInFinder = true; // re-point the handler if the exe has moved
        if (openFolder != null) FinderWindow.Open(openFolder);

        Settings.Changed += () =>
        {
            Resources["UiFont"] = new FontFamily(Settings.Current.FontFamily);
            ShortcutHook.Apply();
            if (!ShellMode)
            {
                if (Settings.Current.HideWindowsTaskbar) Sys.HideTaskbar(); else Sys.ShowTaskbar();
            }
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_primary) return;
        WorkArea.ReleaseAll();
        Sys.ShowTaskbar();
        base.OnExit(e);
    }

    public static void Quit() => Current.Shutdown();

    public const string BarTitle = "UnixLauncher.MenuBar";

    /// <summary>Hand a folder to the copy that's already running (its menu bar window receives WM_COPYDATA).</summary>
    static void SendToRunningCopy(string folder)
    {
        IntPtr bar = Native.FindWindow(null, BarTitle);
        if (bar == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(bar, out uint pid);
        Native.AllowSetForegroundWindow(pid); // we were just launched by the user, so we may pass focus on
        IntPtr text = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(folder);
        try
        {
            var data = new Native.COPYDATASTRUCT { dwData = new IntPtr(1), cbData = (folder.Length + 1) * 2, lpData = text };
            Native.SendCopyData(bar, Native.WM_COPYDATA, IntPtr.Zero, ref data);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(text); }
    }

    public static void Log(object? error)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.AppendAllText(Path.Combine(Settings.Dir, "error.log"), $"[{DateTime.Now:u}] {error}\n\n");
        }
        catch { /* logging must never throw */ }
    }
}
