using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
            ApplyBackground();
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
        FocusBtn.Content = UI.Icon("focus", "\uE708", 13);
        BtBtn.Content = UI.Icon("bluetooth", "\uE702", 13);
        Settings.Changed += () => { ApplyBackground(); Refresh(force: true); };
        _menuTracking.Tick += (_, _) => TrackMenus();
        Loaded += (_, _) => // macOS keeps a menu-bar item highlighted while its panel is open
        {
            Highlight(App.ControlCentre, CcBtn);
            Highlight(App.NotificationCentre, ClockBtn);
            Highlight(App.Spotlight, SearchBtn);
        };

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
        if (msg == Native.WM_COPYDATA) // a folder handed over by a second launch (Finder as folder handler)
        {
            var data = Marshal.PtrToStructure<Native.COPYDATASTRUCT>(l);
            if (data.dwData == new IntPtr(App.OpenMessage) && Marshal.PtrToStringUni(data.lpData) is { Length: > 0 } folder)
                Dispatcher.BeginInvoke(() => FinderWindow.Open(folder)); // let the sender's SendMessage return first
            else if (data.dwData == new IntPtr(App.QuitMessage))
                Dispatcher.BeginInvoke(App.Quit); // the uninstaller asked us to go
            handled = true;
            return new IntPtr(1);
        }
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

    static void Highlight(Window panel, Button item) => panel.IsVisibleChanged += (_, _) => item.Tag = panel.IsVisible ? "on" : null;

    void ApplyBackground()
    {
        bool bg = Settings.Current.MenuBarBackground;
        // "Transparent" is alpha 1, not 0: fully transparent pixels would pass clicks through to the window below.
        Background = bg ? (Brush)FindResource("BarBg") : new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        BarContent.Effect = bg ? null : new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.7 }; // readable on bright wallpaper
        if (_hwnd != IntPtr.Zero) Native.EnableBlur(_hwnd, bg);
    }

    static Visibility Vis(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Is a Control Centre module in the menu bar right now (Settings › Control Centre)?</summary>
    static bool Shown(string module, bool active = true) => Settings.Current.MenuBarMode(module) switch
    {
        Settings.Show => true,
        Settings.WhenActive => active,
        _ => false,
    };

    void Refresh(bool force = false)
    {
        var now = DateTime.Now;
        ClockBtn.Content = $"{now:ddd d MMM}  {now:t}";
        if (_tick++ % 10 != 0 && !force) return; // status icons every 10 s

        bool online = NetworkInterface.GetIsNetworkAvailable();
        WifiBtn.Opacity = online ? 1 : 0.4;
        WifiBtn.ToolTip = Network.WifiName() ?? (online ? "Connected" : "Not connected");
        WifiBtn.Visibility = Vis(Shown("Wi-Fi"));
        FocusBtn.Visibility = Vis(Shown("Focus", Settings.Current.DoNotDisturb));
        SearchBtn.Visibility = Vis(Shown("Spotlight"));

        var vol = Volume.Get();
        SoundBtn.Visibility = Vis(vol != null && Shown("Sound"));
        if (vol != null) SoundBtn.Content = UI.Icon("sound", vol == 0 ? "\uE74F" : "\uE767", 13); // Mute / Volume

        BatteryBtn.Visibility = Visibility.Collapsed;
        _ = RefreshAsync();
        if (Shown("Battery") && Battery.Get() is { } b)
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

    /// <summary>Bluetooth and Now Playing state come from async Windows APIs.</summary>
    async Task RefreshAsync()
    {
        bool? bt = Shown("Bluetooth") ? await Radios.IsOn(true) : null;
        BtBtn.Visibility = Vis(bt != null);
        BtBtn.Opacity = bt == true ? 1 : 0.4;

        var np = Shown("Now Playing") ? await Media.Current() : null;
        NowPlayingBtn.Visibility = Vis(np != null);
        if (np == null) return;
        NowPlayingBtn.Content = UI.Icon(np.Playing ? "media-play" : "media-pause", np.Playing ? "\uE768" : "\uE769", 13);
        NowPlayingBtn.ToolTip = string.IsNullOrEmpty(np.Artist) ? np.Title : $"{np.Title} — {np.Artist}";
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
            var own = HwndSource.FromHwnd(fg)?.RootVisual;
            if (own is FinderWindow fw) ShowFinder(fw, fg);
            else if (own is SettingsWindow sw) ShowOwnApp(sw, "Launcher Settings");
            return;
        }
        if (Native.IsShellWindow(fg)) { ShowFinder(null, IntPtr.Zero); return; } // on macOS the desktop belongs to Finder
        if (!RunningApps.IsAppWindow(fg)) return; // hidden helpers (svchost's off-screen window after unlock), dialogs: keep the app
        if (fg == _target && !_finder && _ownApp == null) return;
        _finder = false;
        _ownApp = null;
        _target = fg;
        _targetInfo = RunningApps.Describe(fg);
        AppBtn.Content = RunningApps.DisplayName(_targetInfo);
        BuildAppMenus();
    }

    // ---------- macOS menu tracking: while one menu is open, pointing at another title opens that one ----------
    ContextMenu? _openMenu;
    UIElement? _openFrom;
    readonly DispatcherTimer _menuTracking = new() { Interval = TimeSpan.FromMilliseconds(40) };

    void ShowBarMenu(ContextMenu menu, UIElement from)
    {
        _openMenu = menu;
        _openFrom = from;
        menu.Closed += (_, _) => { if (_openMenu == menu) _openMenu = null; };
        UI.ShowMenu(menu, from);
        _menuTracking.Start();
    }

    void TrackMenus()
    {
        if (_openMenu is not { IsOpen: true } open) { _menuTracking.Stop(); return; }
        if (!Native.GetCursorPos(out var p)) return;
        foreach (var title in new[] { LogoBtn, AppBtn }.Concat(AppMenus.Children.OfType<Button>()))
        {
            if (title == _openFrom || !title.IsVisible || !Contains(title, p)) continue;
            open.IsOpen = false;
            title.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            return;
        }
    }

    static bool Contains(FrameworkElement el, Native.POINT p)
    {
        var a = el.PointToScreen(new Point(0, 0));
        var b = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
        return p.X >= a.X && p.X < b.X && p.Y >= a.Y && p.Y < b.Y;
    }

    bool _finder; // the bar is showing Finder (a Finder window or the desktop is in front)
    Window? _ownApp; // the bar is showing one of the launcher's own windows (Settings)

    void ShowOwnApp(Window w, string name)
    {
        if (_ownApp == w) return;
        _finder = false;
        _ownApp = w;
        _target = UI.Hwnd(w);
        _targetInfo = null;
        AppBtn.Content = name;
        AppMenus.Children.Clear();
        var window = new Button { Style = (Style)FindResource("BarButton"), Content = "Window" };
        window.Click += (_, _) => ShowBarMenu(UI.Menu(
            UI.Item("Minimise", () => { if (w is MacWindow mac) mac.Minimise(); else w.WindowState = WindowState.Minimized; }),
            UI.Item("Zoom", () => (w as MacWindow)?.Zoom()),
            null,
            UI.Item("Close Window", w.Close, keys: "Ctrl+W")), window);
        AppMenus.Children.Add(window);
    }

    void ShowFinder(FinderWindow? fw, IntPtr hwnd)
    {
        if (_finder && hwnd == _target) return; // the desktop (Zero) included: rebuilding would pull an open menu's title out from under it
        _finder = true;
        _ownApp = null;
        _target = hwnd;
        _targetInfo = null;
        AppBtn.Content = "Finder";
        AppMenus.Children.Clear();
        foreach (var (title, make) in FinderWindow.MenuBarMenus(fw))
        {
            var btn = new Button { Style = (Style)FindResource("BarButton"), Content = title };
            btn.Click += (_, _) => ShowBarMenu(make(), btn);
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
        window.Click += (_, _) => ShowBarMenu(WindowMenu(target), window);
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
            UI.Item("Minimise", () => Native.SysCommand(target, Native.SC_MINIMIZE), keys: UI.MacOnly("Alt+M")),
            UI.Item("Zoom", () => Native.SysCommand(target, Native.IsZoomed(target) ? Native.SC_RESTORE : Native.SC_MAXIMIZE)),
            UI.Item("Fill", () => Place(target, 0, 0, 1, 1)),
            UI.Item("Centre", () => Centre(target)),
            null,
            UI.Sub("Move & Resize",
                UI.Item("Left", () => Place(target, 0, 0, 0.5, 1)),
                UI.Item("Right", () => Place(target, 0.5, 0, 0.5, 1)),
                UI.Item("Top", () => Place(target, 0, 0, 1, 0.5)),
                UI.Item("Bottom", () => Place(target, 0, 0.5, 1, 0.5)),
                null,
                UI.Item("Top Left", () => Place(target, 0, 0, 0.5, 0.5)),
                UI.Item("Top Right", () => Place(target, 0.5, 0, 0.5, 0.5)),
                UI.Item("Bottom Left", () => Place(target, 0, 0.5, 0.5, 0.5)),
                UI.Item("Bottom Right", () => Place(target, 0.5, 0.5, 0.5, 0.5))),
            null,
            UI.Item("Bring All to Front", () => { foreach (var w in Enumerable.Reverse(wins)) Native.Activate(w.Hwnd); }),
            UI.Item("Cycle Through Windows", () => Native.Activate(wins[^1].Hwnd), enabled: wins.Count > 1, keys: UI.MacOnly("Alt+`")),
            null,
        };
        foreach (var w in wins)
        {
            var h = w.Hwnd;
            items.Add(UI.Item(w.Title.Length > 60 ? w.Title[..60] + "…" : w.Title, () => Native.Activate(h), isChecked: h == target));
        }
        items.Add(null);
        items.Add(UI.Item("Close Window", () => Native.SysCommand(target, Native.SC_CLOSE), keys: "Ctrl+W"));
        return UI.Menu(items.ToArray());
    }

    /// <summary>
    /// macOS Window › Fill and Move &amp; Resize: put the window on a fraction of its screen's usable area (between the menu bar
    /// and the dock). Windows' frames have invisible resize borders, so the visible frame is what gets placed.
    /// </summary>
    static void Place(IntPtr h, double fx, double fy, double fw, double fh)
    {
        if (WorkAreaOf(h) is not { } wa) return;
        if (Native.IsZoomed(h) || Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
        var (l, t, r, b) = Borders(h);
        int W = wa.Right - wa.Left, H = wa.Bottom - wa.Top;
        int x = wa.Left + (int)(W * fx), y = wa.Top + (int)(H * fy), w = (int)(W * fw), ht = (int)(H * fh);
        Native.SetWindowPos(h, IntPtr.Zero, x - l, y - t, w + l + r, ht + t + b, 0x4 | Native.SWP_NOACTIVATE); // NOZORDER
    }

    static void Centre(IntPtr h)
    {
        if (WorkAreaOf(h) is not { } wa) return;
        if (Native.IsZoomed(h)) Native.ShowWindow(h, Native.SW_RESTORE);
        var f = Native.FrameBounds(h);
        var (l, t, _, _) = Borders(h);
        int x = wa.Left + (wa.Right - wa.Left - (f.Right - f.Left)) / 2, y = wa.Top + (wa.Bottom - wa.Top - (f.Bottom - f.Top)) / 2;
        Native.SetWindowPos(h, IntPtr.Zero, x - l, y - t, 0, 0, Native.SWP_NOSIZE | 0x4 | Native.SWP_NOACTIVATE);
    }

    static Native.RECT? WorkAreaOf(IntPtr h)
    {
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        return Native.GetMonitorInfo(Native.MonitorFromWindow(h, 2), ref mi) ? mi.rcWork : null; // MONITOR_DEFAULTTONEAREST
    }

    static (int l, int t, int r, int b) Borders(IntPtr h)
    {
        Native.GetWindowRect(h, out var outer);
        var vis = Native.FrameBounds(h);
        return (vis.Left - outer.Left, vis.Top - outer.Top, outer.Right - vis.Right, outer.Bottom - vis.Bottom);
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
        ShowBarMenu(UI.Menu(
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
            UI.Item("Apps", App.Launchpad.Toggle),
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
        if (_ownApp != null)
        {
            ShowBarMenu(UI.Menu(
                UI.Item("About UnixLauncher", () => new AboutWindow().Show()),
                null,
                UI.Item(App.ShellMode ? "Quit Launcher and Start Explorer" : "Quit Launcher", QuitLauncher)), AppBtn);
            return;
        }
        if (_finder || _target == IntPtr.Zero || !Native.IsWindow(_target) || _targetInfo is not { } info)
        {
            ShowBarMenu(UI.Menu(
                UI.Item("About Finder", () => MessageBox.Show("Finder\n\nThe macOS-style file browser that comes with UnixLauncher.", "About Finder")),
                null,
                UI.Item("Empty Recycle Bin…", Sys.EmptyTrash, enabled: Sys.TrashHasItems()),
                null,
                UI.Item("Hide Finder", () => { foreach (var f in FinderWindow.All) f.Minimise(); }, keys: UI.MacOnly("Alt+H")),
                UI.Item("Hide Others", () => HideOthers("")),
                UI.Item("Show All", ShowAll)), AppBtn);
            return;
        }
        string name = AppBtn.Content as string ?? "App";
        bool hasExe = info.Exe != null && !IsFrameHost(info.Exe);
        ShowBarMenu(UI.Menu(
            UI.Item($"About {name}", () => About(name, info.Exe!), enabled: hasExe),
            null,
            UI.Item($"Hide {name}", () => HideApp(info.Key), keys: UI.MacOnly("Alt+H")),
            UI.Item("Hide Others", () => HideOthers(info.Key)),
            UI.Item("Show All", ShowAll),
            null,
            UI.Item("Show in Finder", () => Sys.ShowInFinder(info.Exe!), enabled: hasExe),
            null,
            UI.Item($"Quit {name}", () => QuitApp(info.Key), keys: Settings.Current.MacShortcuts ? "Alt+Q" : "Alt+F4")), AppBtn);
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
