using System;
using System.IO;
using System.Linq;
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
        // Windows Settings › Apps › Uninstall runs this; it must work even while another copy is running.
        if (e.Args.Contains("--uninstall")) { Uninstaller.Run(quiet: e.Args.Contains("--quiet")); Shutdown(); return; }

        // "--open <folder>" comes from the folder handler (Settings › Windows Integration).
        int at = Array.IndexOf(e.Args, "--open");
        string? openFolder = at >= 0 && at + 1 < e.Args.Length ? e.Args[at + 1] : null;

        _single = new Mutex(true, InstanceMutex, out _primary);
        if (!_primary)
        {
            if (openFolder != null) SendToRunningCopy(openFolder);
            Shutdown();
            return;
        }

        // A shell must not die on one bad handler: log and keep going.
        DispatcherUnhandledException += (_, ex) => { Log(ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => { Log(ex.ExceptionObject); Sys.ShowTaskbar(); Views.NativeButtons.RestoreAll(); WindowEffects.Stop(); };

        Settings.Load();
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ContextMenu), System.Windows.Controls.ContextMenu.OpenedEvent,
            new RoutedEventHandler((s, _) => { UI.GlassMenu((System.Windows.Controls.ContextMenu)s); UI.CloseOnOutsideClick((System.Windows.Controls.ContextMenu)s); }));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.MenuItem), System.Windows.Controls.MenuItem.SubmenuOpenedEvent,
            new RoutedEventHandler((s, e) =>
            {
                if (e.OriginalSource != s || s is not System.Windows.Controls.MenuItem mi) return;
                if (mi.Template.FindName("PART_Popup", mi) is System.Windows.Controls.Primitives.Popup { Child: System.Windows.Controls.Border panel })
                    Dispatcher.BeginInvoke(() => UI.GlassPanel(panel), System.Windows.Threading.DispatcherPriority.Loaded); // once it has a size
            }));
        Sys.RegisterUninstaller(); // keeps the Apps list entry pointing at wherever this copy now lives
        Views.NativeButtons.RestoreLeftovers(); // if the last run didn't exit cleanly, apps get their title bar buttons back
        Native.UseDarkMenus();
        Resources["UiFont"] = new FontFamily(Settings.Current.FontFamily);
        UseWindowsAccent();

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
        WindowEffects.Start();

        Notifications.Start();
        Notifications.Arrived += n =>
        {
            // Explorer shows its own toasts; we only need banners when we *are* the shell.
            if (ShellMode && !Settings.Current.DoNotDisturb) Banner.Show(n);
        };

        ShortcutHook.Apply();
        HotCorners.Start();
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
        Views.NativeButtons.RestoreAll();
        WindowEffects.Stop();
        base.OnExit(e);
    }

    public static void Quit() => Current.Shutdown();

    /// <summary>
    /// Highlights, switches and sidebar icons use the Windows accent colour, as macOS uses its accent colour. Set before any
    /// window loads: the theme looks "Accent" up once.
    /// </summary>
    void UseWindowsAccent()
    {
        try
        {
            var c = new global::Windows.UI.ViewManagement.UISettings().GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Accent);
            foreach (var d in Resources.MergedDictionaries)
                if (d.Contains("Accent")) d["Accent"] = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        }
        catch (Exception ex) { Log(ex); } // keep the theme's macOS blue
    }

    public const string BarTitle = "UnixLauncher.MenuBar", InstanceMutex = "UnixLauncher.SingleInstance";
    public const int OpenMessage = 1, QuitMessage = 2; // WM_COPYDATA kinds sent to the running copy

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
            var data = new Native.COPYDATASTRUCT { dwData = new IntPtr(OpenMessage), cbData = (folder.Length + 1) * 2, lpData = text };
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
