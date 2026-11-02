using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
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
        ClockBtn.Content = $"{now:ddd d MMM}  {now:t}";
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

        if (fg == IntPtr.Zero) return;
        if (RunningApps.IsOwn(fg))
        {
            // Finder windows get Finder's menus; other launcher windows (Settings, Quick Look…) leave the bar as it was.
            if (HwndSource.FromHwnd(fg)?.RootVisual is FinderWindow fw) ShowFinder(fw, fg);
            return;
        }
        if (Native.IsShellWindow(fg)) { ShowFinder(null, IntPtr.Zero); return; } // on macOS the desktop belongs to Finder
        if (fg == _target && !_finder) return;
        _finder = false;
        _target = fg;
        _targetInfo = RunningApps.Describe(fg);
        AppBtn.Content = RunningApps.DisplayName(_targetInfo);
        BuildAppMenus();
    }

    bool _finder; // the bar is showing Finder (a Finder window or the desktop is in front)

    void ShowFinder(FinderWindow? fw, IntPtr hwnd)
    {
        if (_finder && hwnd == _target && hwnd != IntPtr.Zero) return;
        _finder = true;
        _target = hwnd;
        _targetInfo = null;
        AppBtn.Content = "Finder";
        AppMenus.Children.Clear();
        foreach (var (title, make) in FinderWindow.MenuBarMenus(fw))
        {
            var btn = new Button { Style = (Style)FindResource("BarButton"), Content = title };
            btn.Click += (_, _) => UI.ShowMenu(make(), btn);
            AppMenus.Children.Add(btn);
        }
    }

    // ---------- The app's own menus, mirrored like the macOS global menu bar ----------
    // shortcut: only classic Win32 menu bars (GetMenu) can be read; apps that draw their own (browsers, UWP,
    // Electron, Office ribbon) get just the Window menu. Owner-drawn items show blank.
    void BuildAppMenus()
    {
        AppMenus.Children.Clear();
        if (_target == IntPtr.Zero) return;
        var target = _target;
        IntPtr menu = Native.GetMenu(target);
        int count = menu == IntPtr.Zero ? 0 : Native.GetMenuItemCount(menu);
        for (int i = 0; i < count; i++)
        {
            IntPtr sub = Native.GetSubMenu(menu, i);
            var sb = new StringBuilder(128);
            Native.GetMenuString(menu, (uint)i, sb, sb.Capacity, 0x400); // MF_BYPOSITION
            string title = MenuText.Clean(sb.ToString());
            if (sub == IntPtr.Zero || title.Length == 0) continue;
            int pos = i;
            var btn = new Button { Style = (Style)FindResource("BarButton"), Content = title };
            btn.Click += (_, _) => ShowNativeMenu(target, menu, sub, pos, btn);
            AppMenus.Children.Add(btn);
        }
        var window = new Button { Style = (Style)FindResource("BarButton"), Content = "Window" };
        window.Click += (_, _) => UI.ShowMenu(WindowMenu(target), window);
        AppMenus.Children.Add(window);
    }

    HwndSource? _menuOwner;

    void ShowNativeMenu(IntPtr target, IntPtr menu, IntPtr sub, int pos, FrameworkElement under)
    {
        // Same two messages Windows sends before opening an app's own menu, so it refreshes ticks and greyed-out items.
        Native.SendMessageTimeout(target, 0x0116, menu, IntPtr.Zero, 0x2, 300, out _);        // WM_INITMENU, SMTO_ABORTIFHUNG
        Native.SendMessageTimeout(target, 0x0117, sub, new IntPtr(pos), 0x2, 300, out _);    // WM_INITMENUPOPUP
        var pt = under.PointToScreen(new Point(0, under.ActualHeight));
        // The owner must be the foreground window, or the menu ignores the keyboard and won't close on an
        // outside click. The bar itself never takes focus, so use a hidden helper (the tray-icon pattern).
        _menuOwner ??= new HwndSource(new HwndSourceParameters("UnixLauncher menu owner")
        {
            Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80, // WS_POPUP (hidden), WS_EX_TOOLWINDOW
        });
        var owner = _menuOwner.Handle;
        Native.SetForegroundWindow(owner);
        int cmd = Native.TrackPopupMenuEx(sub, 0x100 | 0x80, (int)pt.X, (int)pt.Y, owner, IntPtr.Zero); // TPM_RETURNCMD | TPM_NONOTIFY
        Native.PostMessage(owner, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL: documented fix so the next click isn't swallowed
        Native.Activate(target); // hand focus straight back, as macOS does
        if (cmd != 0) Native.PostMessage(target, 0x0111, new IntPtr(cmd), IntPtr.Zero); // WM_COMMAND
    }

    static ContextMenu WindowMenu(IntPtr target)
    {
        var key = RunningApps.Describe(target).Key;
        var wins = RunningApps.Enumerate().Where(w => w.Key == key).ToList();
        var items = new System.Collections.Generic.List<MenuItem?>
        {
            UI.Item("Minimise", () => Native.SysCommand(target, Native.SC_MINIMIZE)),
            UI.Item("Zoom", () => Native.SysCommand(target, Native.IsZoomed(target) ? Native.SC_RESTORE : Native.SC_MAXIMIZE)),
            null,
            UI.Item("Bring All to Front", () => { foreach (var w in Enumerable.Reverse(wins)) Native.Activate(w.Hwnd); }),
            null,
        };
        foreach (var w in wins)
        {
            var h = w.Hwnd;
            items.Add(UI.Item(w.Title.Length > 60 ? w.Title[..60] + "…" : w.Title, () => Native.Activate(h), isChecked: h == target));
        }
        items.Add(null);
        items.Add(UI.Item("Close Window", () => Native.SysCommand(target, Native.SC_CLOSE)));
        return UI.Menu(items.ToArray());
    }

    // ---------- Logo menu ----------
    void Logo_Click(object sender, RoutedEventArgs e)
    {
        MenuItem?[] taskbarItems = App.ShellMode ? [] :
        [
            UI.Item("Windows Start Menu", () => Native.PressKeys(0x5B), keys: "Win"),
            UI.Item(Sys.TaskbarHidden ? "Show Windows Taskbar" : "Hide Windows Taskbar", () => { if (Sys.TaskbarHidden) Sys.ShowTaskbar(); else Sys.HideTaskbar(); }),
        ];
        // Same order as the Apple menu; the launcher's own items are grouped at the end.
        UI.ShowMenu(UI.Menu(
        [
            UI.Item("About This PC", () => new AboutWindow().Show()),
            null,
            UI.Item("System Settings…", () => Sys.Launch("ms-settings:")),
            UI.Item("App Store…", () => Sys.Launch("ms-windows-store:")),
            null,
            UI.Item("Force Quit…", () => Sys.Launch("taskmgr.exe"), keys: "Ctrl+Shift+Esc"),
            null,
            UI.Item("Sleep", Sys.Sleep),
            UI.Item("Restart…", Sys.Restart),
            UI.Item("Shut Down…", Sys.PowerOff),
            null,
            UI.Item("Lock Screen", Sys.Lock, keys: "Win+L"),
            UI.Item($"Log Out {Environment.UserName}…", Sys.LogOut),
            null,
            UI.Item("Launcher Settings…", SettingsWindow.Open),
            UI.Item("Launchpad", App.Launchpad.Toggle),
            .. taskbarItems,
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
        if (_finder || _target == IntPtr.Zero || !Native.IsWindow(_target) || _targetInfo is not { } info)
        {
            UI.ShowMenu(UI.Menu(
                UI.Item("About Finder", () => MessageBox.Show("Finder\n\nThe macOS-style file browser that comes with UnixLauncher.", "About Finder")),
                null,
                UI.Item("Empty Recycle Bin…", Sys.EmptyTrash, enabled: Sys.TrashHasItems()),
                null,
                UI.Item("Hide Finder", () => { foreach (var f in FinderWindow.All) f.WindowState = WindowState.Minimized; }),
                UI.Item("Hide Others", () => HideOthers("")),
                UI.Item("Show All", ShowAll)), AppBtn);
            return;
        }
        string name = AppBtn.Content as string ?? "App";
        bool hasExe = info.Exe != null && !IsFrameHost(info.Exe);
        UI.ShowMenu(UI.Menu(
            UI.Item($"About {name}", () => About(name, info.Exe!), enabled: hasExe),
            null,
            UI.Item($"Hide {name}", () => HideApp(info.Key)),
            UI.Item("Hide Others", () => HideOthers(info.Key)),
            UI.Item("Show All", ShowAll),
            null,
            UI.Item("Show in Finder", () => Sys.ShowInFinder(info.Exe!), enabled: hasExe),
            null,
            UI.Item($"Quit {name}", () => QuitApp(info.Key), keys: "Alt+F4")), AppBtn);
    }

    static bool IsFrameHost(string exe) => exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>macOS's "About App" box, filled from the exe's version resource.</summary>
    static void About(string name, string exe)
    {
        var v = FileVersionInfo.GetVersionInfo(exe);
        MessageBox.Show($"{name}\n\nVersion {v.ProductVersion}\n{v.CompanyName}\n{v.LegalCopyright}".TrimEnd(), $"About {name}");
    }

    // Windows has no "hidden" app state; minimising all of its windows is the closest match.
    public static void HideApp(string key)
    {
        foreach (var w in RunningApps.Enumerate())
            if (w.Key == key) Native.SysCommand(w.Hwnd, Native.SC_MINIMIZE);
    }

    static void HideOthers(string key)
    {
        foreach (var w in RunningApps.Enumerate())
            if (w.Key != key && !Native.IsIconic(w.Hwnd)) Native.SysCommand(w.Hwnd, Native.SC_MINIMIZE);
    }

    static void ShowAll()
    {
        foreach (var w in RunningApps.Enumerate())
            if (Native.IsIconic(w.Hwnd)) Native.ShowWindow(w.Hwnd, 4); // SW_SHOWNOACTIVATE
    }

    public static void QuitApp(string key)
    {
        foreach (var w in RunningApps.Enumerate())
            if (w.Key == key) Native.PostMessage(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    public static void ForceQuit(IntPtr hwnd, string name)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint framePid);
        // Killing the Store-app frame host would take every Store app down with it.
        if (IsFrameHost(Native.GetProcessPath(framePid) ?? "")) { MessageBox.Show($"{name} can't be force quit from here; use Task Manager.", "UnixLauncher"); return; }
        if (MessageBox.Show($"Force quit {name}? Unsaved changes will be lost.", "UnixLauncher", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        try { Process.GetProcessById((int)pid).Kill(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "UnixLauncher"); }
    }

    void Clock_Click(object sender, RoutedEventArgs e) => App.NotificationCentre.Toggle();
    void ControlCentre_Click(object sender, RoutedEventArgs e) => App.ControlCentre.Toggle();
    void Search_Click(object sender, RoutedEventArgs e) => App.Spotlight.Toggle();
}
