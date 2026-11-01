using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace UnixLauncher.Views;

public partial class MenuBar : Window
{
    const double BarHeight = 26;
    const int HotkeySpotlight = 1;

    IntPtr _hwnd;
    IntPtr _target;        // app window the app menu acts on (we never take focus, so it stays the foreground)
    WinInfo? _targetInfo;
    int _tick;

    public MenuBar()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = UI.Hwnd(this);
            Native.SetToolWindow(_hwnd, true);
            Native.EnableBlur(_hwnd);
            HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
            // shortcut: hotkey fixed to Alt+Space; make it a setting if people use PowerToys Run.
            if (!Native.RegisterHotKey(_hwnd, HotkeySpotlight, 0x1 | 0x4000, 0x20)) // MOD_ALT | MOD_NOREPEAT, VK_SPACE
                App.Log("Alt+Space is used by another app; Spotlight hotkey not registered.");
            Layout();
        };
        Closed += (_, _) => Native.UnregisterHotKey(_hwnd, HotkeySpotlight);

        if (Icons.Override("menu-logo") is { } logo) LogoBtn.Content = new Image { Source = logo, Width = 15, Height = 15 };
        SearchBtn.Content = UI.Icon("search", "", 13);
        CcBtn.Content = UI.Icon("control-centre", "", 13);
        WifiBtn.Content = UI.Icon("wifi", "", 13);
        FocusBtn.Content = UI.Icon("focus", "", 13);

        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => Refresh();
        clock.Start();
        Refresh();
        WindowEvents.Foreground += OnForeground;
    }

    public void Layout()
    {
        int w = Native.GetSystemMetrics(0), h = (int)Math.Round(BarHeight * UI.Scale(this));
        WorkArea.Reserve(_hwnd, Native.ABE_TOP, h);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, w, h, Native.SWP_NOACTIVATE);
    }

    static readonly uint TaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && w.ToInt32() == HotkeySpotlight) App.Spotlight.Toggle();
        if ((uint)msg == TaskbarCreated) // Explorer restarted: it forgot our reserved space and re-showed its taskbar
        {
            WorkArea.ReleaseAll();
            if (!App.ShellMode && Settings.Current.HideWindowsTaskbar) Sys.HideTaskbar();
            Layout();
            App.Dock.Layout();
        }
        if (msg == 0x007E) // WM_DISPLAYCHANGE
        {
            Layout();
            App.Dock.Layout();
        }
        return IntPtr.Zero;
    }

    void Refresh()
    {
        var now = DateTime.Now;
        ClockBtn.Content = $"{now:ddd d MMM}   {now:t}";
        if (_tick++ % 10 != 0) return; // status icons every 10 s

        bool online = NetworkInterface.GetIsNetworkAvailable();
        WifiBtn.Opacity = online ? 1 : 0.4;
        WifiBtn.ToolTip = online ? "Connected" : "Not connected";
        FocusBtn.Visibility = Settings.Current.DoNotDisturb ? Visibility.Visible : Visibility.Collapsed;

        if (Battery.Get() is { } b)
        {
            int level = Math.Clamp(b.percent / 10, 0, 10);
            var glyph = ((char)((b.charging ? 0xEBAB : 0xEBA0) + level)).ToString(); // Battery0..10 / BatteryCharging0..10
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock { Text = $"{b.percent}%", Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(UI.Icon(b.charging ? "battery-charging" : "battery", glyph, 14));
            BatteryBtn.Content = panel;
            BatteryBtn.Visibility = Visibility.Visible;
        }
    }

    void OnForeground(IntPtr fg)
    {
        Sys.ReassertTaskbar();
        bool fullscreen = Native.IsFullscreen(fg) && !RunningApps.IsOwn(fg);
        if (fullscreen == IsVisible) { if (fullscreen) Hide(); else Show(); } // macOS hides bar + dock for full-screen apps
        App.Dock.SetHiddenForFullscreen(fullscreen);

        if (fg == IntPtr.Zero || RunningApps.IsOwn(fg)) return;
        if (Native.IsShellWindow(fg)) { _target = IntPtr.Zero; _targetInfo = null; AppBtn.Content = "Desktop"; return; }
        _target = fg;
        _targetInfo = RunningApps.Describe(fg);
        AppBtn.Content = RunningApps.DisplayName(_targetInfo);
    }

    // ---------- Logo menu ----------
    void Logo_Click(object sender, RoutedEventArgs e)
    {
        MenuItem?[] taskbarItems = App.ShellMode ? [] :
        [
            UI.Item("Start Menu", Native.PressWinKey),
            UI.Item(Sys.TaskbarHidden ? "Show Windows Taskbar" : "Hide Windows Taskbar", () => { if (Sys.TaskbarHidden) Sys.ShowTaskbar(); else Sys.HideTaskbar(); }),
        ];
        UI.ShowMenu(UI.Menu(
        [
            UI.Item("About This PC", () => new AboutWindow().Show()),
            null,
            UI.Item("System Settings…", () => Sys.Launch("ms-settings:")),
            UI.Item("Launcher Settings…", SettingsWindow.Open),
            UI.Item("App Store…", () => Sys.Launch("ms-windows-store:")),
            null,
            UI.Item("Launchpad", App.Launchpad.Toggle),
            .. taskbarItems,
            null,
            UI.Item("Force Quit…", () => Sys.Launch("taskmgr.exe")),
            null,
            UI.Item("Sleep", Sys.Sleep),
            UI.Item("Restart…", Sys.Restart),
            UI.Item("Shut Down…", Sys.PowerOff),
            null,
            UI.Item("Lock Screen", Sys.Lock),
            UI.Item("Log Out…", Sys.LogOut),
            null,
            UI.Item(App.ShellMode ? "Quit Launcher and Start Explorer" : "Quit Launcher", QuitLauncher),
        ]), LogoBtn);
    }

    static void QuitLauncher()
    {
        if (App.ShellMode)
        {
            if (MessageBox.Show("UnixLauncher is your shell. Quit and start Windows Explorer instead?", "UnixLauncher", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            Process.Start("explorer.exe");
        }
        App.Quit();
    }

    // ---------- Active app menu ----------
    void App_Click(object sender, RoutedEventArgs e)
    {
        if (_target == IntPtr.Zero || !Native.IsWindow(_target) || _targetInfo is not { } info)
        {
            UI.ShowMenu(UI.Menu(UI.Item("New File Explorer Window", () => Sys.Launch("explorer.exe"))), AppBtn);
            return;
        }
        var target = _target;
        string name = AppBtn.Content as string ?? "App";
        bool frameHost = info.Exe?.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) == true;
        UI.ShowMenu(UI.Menu(
            UI.Item("Minimise", () => Native.SysCommand(target, Native.SC_MINIMIZE)),
            UI.Item("Zoom", () => Native.SysCommand(target, Native.IsZoomed(target) ? Native.SC_RESTORE : Native.SC_MAXIMIZE)),
            UI.Item("Close Window", () => Native.SysCommand(target, Native.SC_CLOSE)),
            null,
            UI.Item("Show in File Explorer", () => Sys.ShowInExplorer(info.Exe!), enabled: info.Exe != null && !frameHost),
            null,
            UI.Item($"Quit {name}", () => QuitApp(info.Key)),
            // Killing the Store-app frame host would take every Store app down with it.
            UI.Item($"Force Quit {name}", () => ForceQuit(target, name), enabled: !frameHost)), AppBtn);
    }

    public static void QuitApp(string key)
    {
        foreach (var w in RunningApps.Enumerate())
            if (w.Key == key) Native.PostMessage(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    static void ForceQuit(IntPtr hwnd, string name)
    {
        if (MessageBox.Show($"Force quit {name}? Unsaved changes will be lost.", "UnixLauncher", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        try { Process.GetProcessById((int)pid).Kill(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "UnixLauncher"); }
    }

    void Clock_Click(object sender, RoutedEventArgs e) => App.NotificationCentre.Toggle();
    void ControlCentre_Click(object sender, RoutedEventArgs e) => App.ControlCentre.Toggle();
    void Search_Click(object sender, RoutedEventArgs e) => App.Spotlight.Toggle();
}
