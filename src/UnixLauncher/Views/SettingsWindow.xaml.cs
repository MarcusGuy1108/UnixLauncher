using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace UnixLauncher.Views;

/// <summary>Launcher settings, laid out like macOS System Settings: categories on the left, grouped rows on the right.</summary>
public partial class SettingsWindow : MacWindow
{
    static SettingsWindow? _open;
    // Sliders and text boxes fire on every change; save (and re-layout the dock) once the user pauses.
    readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly List<(string Name, Button Button, string Keywords)> _nav = new();
    string _page = "";

    record PageInfo(string Name, string Color, string Glyph, string Keywords, Func<SettingsWindow, FrameworkElement[]> Build);

    static readonly PageInfo[] Pages =
    {
        new("General", "#8E8E93", "", "startup sign in login version log folder", w => w.General()),
        new("Appearance", "#5E5CE6", "", "menu bar background transparent font icons", w => w.Appearance()),
        new("Control Centre", "#8E8E93", "", "menu bar wi-fi bluetooth focus sound now playing battery spotlight modules", w => w.ControlCentrePage()),
        new("Desktop & Dock", "#0A84FF", "", "dock size magnification position hide windows traffic lights buttons hot corners mission control", w => w.DockPage()),
        new("Keyboard", "#8E8E93", "\uE765", "keyboard command cmd alt mac shortcuts copy paste quit", w => w.KeyboardPage()),
        new("Windows Integration", "#FF9F0A", "", "explorer finder start launchpad search spotlight quick settings notifications taskbar shell replace", w => w.Integration()),
    };

    public static void Open() => Open("General");

    public static void Open(string page)
    {
        if (_open == null)
        {
            _open = new SettingsWindow();
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        _open.WindowState = WindowState.Normal;
        _open.ShowPage(page);
        UI.BringToFront(_open);
    }

    SettingsWindow()
    {
        UnifiedToolbar = true;
        InitializeComponent();
        Activated += (_, _) => HighlightNav();
        Deactivated += (_, _) => HighlightNav();
        SearchIcon.Content = UI.Icon("search", "", 12);
        foreach (var p in Pages) AddNav(p);
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); Settings.Save(); };
        Closed += (_, _) => { if (_saveDebounce.IsEnabled) { _saveDebounce.Stop(); Settings.Save(); } };
        ShowPage("General");
    }

    // ---------- Pages ----------
    FrameworkElement[] General() => new[]
    {
        Group(null,
            Row("Start UnixLauncher when you sign in", Sys.IsRegisteredShell ? "Not needed while UnixLauncher is your shell." : null,
                Switch(Sys.StartsWithWindows, v => Sys.StartsWithWindows = v, enabled: !Sys.IsRegisteredShell))),
        Group("About",
            Row("UnixLauncher", App.ShellMode ? "Running as the Windows shell." : "Running on top of Windows Explorer.", null),
            Row("Settings and logs", Settings.Dir, Btn("Open Folder", () => Process.Start("explorer.exe", $"\"{Settings.Dir}\"")))),
        Group("Uninstall",
            Row("Uninstall UnixLauncher", "Puts back everything it changed (File Explorer, the shell, the taskbar, keyboard shortcuts, " +
                "start at sign-in) and removes it. Also available in Windows Settings › Apps.",
                Btn("Uninstall…", () => Process.Start(Sys.ExePath, "--uninstall")))),
    };

    FrameworkElement[] Appearance() => new[]
    {
        Group(null,
            Row("Show menu bar background", "Off gives the clear macOS 26 menu bar.", Switch(Settings.Current.MenuBarBackground, v => Set(s => s.MenuBarBackground = v)))),
        Group("Font",
            Row("Font family", "SF Pro is used when installed (free from developer.apple.com/fonts); or type any installed font.",
                TextField(Settings.Current.FontFamily, t => { if (t.Trim().Length > 0) Set(s => s.FontFamily = t.Trim()); }))),
        Group("Icons",
            Row("Custom icons", "Drop PNG or ICO files into the icons folder to replace any icon. README.md there lists the names.",
                Btn("Open Folder", OpenIcons)),
            Row("Reload icons", null, Btn("Reload", () => Settings.Save()))), // Settings.Changed clears the icon cache and rebuilds
    };

    FrameworkElement[] ControlCentrePage()
    {
        var rows = Settings.MenuBarModules.Select(m =>
        {
            var (color, glyph) = ModuleIcon(m.Name);
            return Row(m.Name, null, Choice(m.Modes, Settings.Current.MenuBarMode(m.Name), v => Set(s => s.MenuBarItems[m.Name] = v)), color, glyph);
        }).ToArray();
        return new[]
        {
            Note("Control Centre Modules", "These modules are always visible in Control Centre. You can choose when they should also show in the menu bar."),
            Group(null, rows),
        };
    }

    static (string Color, string Glyph) ModuleIcon(string module) => module switch
    {
        "Wi-Fi" => ("#0A84FF", ""),
        "Bluetooth" => ("#0A84FF", ""),
        "Focus" => ("#5E5CE6", ""),
        "Sound" => ("#FF453A", ""),
        "Now Playing" => ("#FF9F0A", ""),
        "Battery" => ("#30D158", ""),
        _ => ("#8E8E93", ""),
    };

    FrameworkElement[] DockPage()
    {
        var s = Settings.Current;
        return new[]
        {
            Group("Dock",
                Row("Size", null, SliderCtl(32, 96, s.DockIconSize, v => Set(x => x.DockIconSize = Math.Round(v)))),
                Row("Magnification", null, Switch(s.DockMagnify, v => Set(x => x.DockMagnify = v))),
                Row("Magnification amount", null, SliderCtl(1.1, 2.5, s.DockMagnification, v => Set(x => x.DockMagnification = Math.Round(v, 2)))),
                Row("Position on screen", null, Choice(new[] { "Left", "Bottom", "Right" }, s.DockPosition, v => Set(x => x.DockPosition = v))),
                Row("Automatically hide and show the Dock", null, Switch(s.DockAutoHide, v => Set(x => x.DockAutoHide = v)))),
            Group("Windows",
                Row("Show red, yellow and green buttons on other apps' windows", "On the focused window's top-left corner. Where the app already has something there (a tab, its icon), they appear in a glass pill when you point at the corner.",
                    Switch(s.TrafficLightOverlay, v => Set(x => x.TrafficLightOverlay = v))),
                Row("Hide Windows' own title bar buttons", "Takes minimise, maximise and close off apps' title bars, the way Windows does for " +
                    "windows without them (snapping and Win+↑ still work). Put back when you switch this off or quit. Apps that draw their " +
                    "own (browsers, Settings, Discord) get a cover in the title bar's colour exactly over them instead.",
                    Switch(s.HideCaptionButtons, v => Set(x => x.HideCaptionButtons = v))),
                Row("Leave these apps alone", "No round title bar buttons, Alt-as-⌘ or Windows-key changes in these apps, e.g. games you play in a " +
                    "window or apps that use Alt+click. Exe names, comma separated, e.g. chrome.exe, blender.exe",
                    TextField(string.Join(", ", s.OverlayExclude),
                        t => Set(x => x.OverlayExclude = t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList())))),
            Group("Hot Corners",
                HotCornerRow("Top Left"), HotCornerRow("Top Right"), HotCornerRow("Bottom Left"), HotCornerRow("Bottom Right")),
        };
    }

    FrameworkElement HotCornerRow(string corner) => Row(corner, null,
        Choice(Settings.HotCornerActions, Settings.Current.HotCorners.GetValueOrDefault(corner, "-"),
            v => Set(s => { if (v == "-") s.HotCorners.Remove(corner); else s.HotCorners[corner] = v; })));

    FrameworkElement[] KeyboardPage() => new[]
    {
        Group(null,
            Row("Use Alt as ⌘ Command", "Alt sits where ⌘ is on a Mac keyboard, so your Mac shortcuts work here too. It steps aside while a " +
                "full-screen game is in front, and in apps listed under Desktop & Dock › Leave these apps alone.",
                Switch(Settings.Current.MacShortcuts, v => Set(x => x.MacShortcuts = v)))),
        Group("Shortcuts",
            Row("⌘C ⌘V ⌘X ⌘Z ⌘A ⌘S ⌘F ⌘T ⌘N ⌘W ⌘P ⌘R ⌘1–9 ⌘= ⌘−", "Alt + the same key (apps receive Ctrl + that key)", null),
            Row("⌘⇧Z  Redo", "Alt+Shift+Z", null),
            Row("⌘Q  Quit app · ⌘H  Hide · ⌘M  Minimise · ⌘`  Next window", "Alt+Q · Alt+H · Alt+M · Alt+`", null),
            Row("⌘← ⌘→  Line start / end · ⌘↑ ⌘↓  Top / bottom · ⌘⌫  Delete to line start", "Alt + arrows / Backspace; hold Shift to select", null),
            Row("⌘[ ⌘]  Back / forward", "Alt+[ and Alt+]", null),
            Row("⌥← ⌥→  Word left / right · ⌥⌫ ⌥⌦  Delete a word", "The Windows key sits where ⌥ is: Win + arrows / Backspace / Delete; hold Shift to select. " +
                "(Win+← and Win+→ no longer snap windows while this is on.)", null),
            Row("⌘⇧3  Screenshot · ⌘⇧4 / ⌘⇧5  Select an area", "Alt+Shift+3, 4, 5", null),
            Row("⌘Space  Spotlight · ⌘Tab  Switch apps", "Alt+Space and Alt+Tab (Windows' own app switcher)", null),
            Row("Finder", "Return renames, ⌘↓ or ⌘O opens, ⌘⌫ moves to the Recycle Bin, ⌘⇧G goes to a folder, ⌘I gets info, " +
                "⌘⇧H / ⌘⇧D / ⌘⇧C open Home / Desktop / This PC, ⌘⇧. shows hidden files.", null)),
    };

    FrameworkElement[] Integration()
    {
        var s = Settings.Current;
        return new[]
        {
            Note("Replace Windows features", "Let UnixLauncher take over parts of Windows. Each can be switched back at any time."),
            Group(null,
                Row("Open folders in Finder", "Folders, drives, This PC and the Recycle Bin open in Finder instead of File Explorer, and so does Win+E.",
                    Switch(s.ReplaceExplorer, SetFinderAsFolderHandler), "#0A84FF", ""),
                Row("Windows key opens Apps", "Instead of the Start menu. Win + other keys still work as usual.",
                    Switch(s.ReplaceStart, v => Set(x => x.ReplaceStart = v)), "#8E8E93", ""),
                Row("Win+S opens Spotlight", "Instead of Windows Search (Win+Q too). Alt+Space always opens Spotlight.",
                    Switch(s.ReplaceSearch, v => Set(x => x.ReplaceSearch = v)), "#8E8E93", ""),
                Row("Win+A opens Control Centre", "Instead of Windows' Quick Settings.",
                    Switch(s.ReplaceQuickSettings, v => Set(x => x.ReplaceQuickSettings = v)), "#8E8E93", ""),
                Row("Win+N opens Notification Centre", "Instead of Windows' notification panel.",
                    Switch(s.ReplaceNotifications, v => Set(x => x.ReplaceNotifications = v)), "#FF453A", "")),
            Group("Taskbar and shell",
                Row("Hide the Windows taskbar", App.ShellMode ? "There is no taskbar while UnixLauncher is your shell." : null,
                    Switch(s.HideWindowsTaskbar, v => Set(x => x.HideWindowsTaskbar = v), enabled: !App.ShellMode)),
                Row("Replace Explorer as the Windows shell",
                    "From your next sign-in, UnixLauncher starts instead of Explorer's desktop and taskbar. If anything goes wrong: " +
                    "Ctrl+Alt+Del › Task Manager › Run new task › explorer.exe, then switch this off.",
                    Switch(Sys.IsRegisteredShell, SetShell))),
        };
    }

    void SetFinderAsFolderHandler(bool on)
    {
        try { Sys.OpensFoldersInFinder = on; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Launcher Settings"); ShowPage(_page); return; }
        Set(x => x.ReplaceExplorer = on);
    }

    void SetShell(bool on)
    {
        if (on && MessageBox.Show(this,
                "UnixLauncher will replace Windows Explorer as your shell the next time you sign in (this user only).\n\n" +
                "If anything goes wrong: press Ctrl+Alt+Del › Task Manager › Run new task › explorer.exe, then switch this off.\n\n" +
                "Don't move or delete UnixLauncher.exe while this is on.\n\nContinue?",
                "Replace Explorer as the shell", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            ShowPage(_page); // put the switch back
            return;
        }
        Sys.SetRegisteredShell(on);
    }

    void OpenIcons()
    {
        Directory.CreateDirectory(Icons.UserDir);
        var readme = Path.Combine(AppContext.BaseDirectory, "icons", "README.md");
        var copy = Path.Combine(Icons.UserDir, "README.md");
        if (File.Exists(readme) && !File.Exists(copy)) File.Copy(readme, copy);
        Sys.Launch(Icons.UserDir);
    }

    // ---------- Navigation ----------
    void AddNav(PageInfo p)
    {
        var row = new DockPanel();
        var icon = Squircle(p.Color, p.Glyph, 20);
        icon.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = p.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var b = new Button { Style = (Style)FindResource("SidebarItem"), Content = row };
        b.Click += (_, _) => ShowPage(p.Name);
        Nav.Children.Add(b);
        _nav.Add((p.Name, b, p.Keywords));
    }

    void ShowPage(string name)
    {
        var p = Array.Find(Pages, x => x.Name == name) ?? Pages[0];
        _page = p.Name;
        PageTitle.Text = p.Name;
        Page.Children.Clear();
        foreach (var el in p.Build(this)) Page.Children.Add(el);
        HighlightNav();
    }

    // macOS: the selected row is in the accent colour while the window is in front, grey behind other windows.
    void HighlightNav()
    {
        var on = IsActive ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromRgb(0x46, 0x46, 0x49));
        foreach (var (n, b, _) in _nav) b.Background = n == _page ? on : Brushes.Transparent;
    }

    void Search_Changed(object sender, TextChangedEventArgs e)
    {
        string q = SearchBox.Text.Trim();
        SearchPlaceholder.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Hidden;
        foreach (var (n, b, keywords) in _nav)
            b.Visibility = q.Length == 0 || n.Contains(q, StringComparison.OrdinalIgnoreCase) || keywords.Contains(q, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
    }

    void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) Zoom(); else DragMove();
    }

    // ---------- Row building blocks ----------
    void Set(Action<Settings> change)
    {
        change(Settings.Current);
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    Brush Res(string key) => (Brush)FindResource(key);

    static FrameworkElement Squircle(string color, string glyph, double size) => new Border
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.26),
        Background = (Brush)new BrushConverter().ConvertFromString(color)!,
        Child = new TextBlock
        {
            Text = glyph, FontSize = size * 0.55, Foreground = Brushes.White, FontFamily = (FontFamily)Application.Current.Resources["GlyphFont"],
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    FrameworkElement Note(string title, string text)
    {
        var sp = new StackPanel { Margin = new Thickness(2, 0, 0, 10) };
        sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        sp.Children.Add(new TextBlock { Text = text, Foreground = Res("SubText"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        return sp;
    }

    /// <summary>A rounded card of rows with hairlines between them, optionally under a heading.</summary>
    FrameworkElement Group(string? header, params FrameworkElement[] rows)
    {
        var list = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) list.Children.Add(new Border { Height = 1, Background = Res("Line"), Margin = new Thickness(12, 0, 0, 0) });
            list.Children.Add(rows[i]);
        }
        var card = new Border { Background = Res("CardBg"), CornerRadius = new CornerRadius(10), Child = list, Margin = new Thickness(0, 0, 0, 18) };
        if (header == null) return card;
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = header, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 6) });
        sp.Children.Add(card);
        return sp;
    }

    FrameworkElement Row(string label, string? help, FrameworkElement? control, string? color = null, string? glyph = null)
    {
        var row = new DockPanel { Margin = new Thickness(12, 8, 12, 8), MinHeight = 26 };
        if (control != null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(16, 0, 0, 0);
            DockPanel.SetDock(control, Dock.Right);
            row.Children.Add(control);
        }
        if (color != null && glyph != null)
        {
            var icon = Squircle(color, glyph, 22);
            icon.Margin = new Thickness(0, 0, 10, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(icon);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (help != null) text.Children.Add(new TextBlock { Text = help, FontSize = 11, Foreground = Res("SubText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        row.Children.Add(text);
        return row;
    }

    CheckBox Switch(bool value, Action<bool> set, bool enabled = true)
    {
        var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = value, IsEnabled = enabled };
        sw.Click += (_, _) => set(sw.IsChecked == true);
        return sw;
    }

    /// <summary>macOS pop-up button: current value with a chevron; click for the choices.</summary>
    FrameworkElement Choice(string[] options, string current, Action<string> set)
    {
        var label = new TextBlock { Text = current, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(label);
        var chevron = UI.Icon("", "", 9);
        chevron.Margin = new Thickness(6, 1, 0, 0);
        content.Children.Add(chevron);
        var btn = new Button { Style = (Style)FindResource("FlatButton"), Content = content, Padding = new Thickness(8, 3, 8, 3) };
        btn.Click += (_, _) => UI.ShowMenu(UI.Menu(options.Select(o => UI.Item(o, () => { label.Text = o; set(o); }, isChecked: o == label.Text)).ToArray()), btn);
        return btn;
    }

    Slider SliderCtl(double min, double max, double value, Action<double> set)
    {
        var s = new Slider { Style = (Style)FindResource("MacSlider"), Minimum = min, Maximum = max, Value = value, Width = 200 };
        s.ValueChanged += (_, e) => set(e.NewValue);
        return s;
    }

    TextBox TextField(string value, Action<string> set)
    {
        var tb = new TextBox { Text = value, Width = 240, Padding = new Thickness(4, 3, 4, 3) };
        tb.TextChanged += (_, _) => set(tb.Text);
        return tb;
    }

    FrameworkElement Btn(string text, Action action)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("FlatButton") };
        b.Click += (_, _) => action();
        return new Border { Background = Res("CardBg"), CornerRadius = new CornerRadius(6), Child = b };
    }
}
