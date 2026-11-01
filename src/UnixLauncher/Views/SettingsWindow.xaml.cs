using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace UnixLauncher.Views;

public partial class SettingsWindow : MacWindow
{
    static SettingsWindow? _open;
    bool _loading = true;
    // Sliders fire on every pixel; save (and re-layout the dock) once the user pauses.
    readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public static void Open()
    {
        if (_open == null)
        {
            _open = new SettingsWindow();
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        _open.WindowState = WindowState.Normal;
        _open.Activate();
    }

    public SettingsWindow()
    {
        InitializeComponent();
        var s = Settings.Current;
        PosLeft.IsChecked = s.DockPosition == "Left";
        PosRight.IsChecked = s.DockPosition == "Right";
        PosBottom.IsChecked = s.DockPosition is not ("Left" or "Right");
        SizeSlider.Value = s.DockIconSize;
        MagnifyBox.IsChecked = s.DockMagnify;
        MagnifySlider.Value = s.DockMagnification;
        AutoHideBox.IsChecked = s.DockAutoHide;
        HideTaskbarBox.IsChecked = s.HideWindowsTaskbar;
        HideTaskbarBox.IsEnabled = !App.ShellMode;
        OverlayBox.IsChecked = s.TrafficLightOverlay;
        ExcludeBox.Text = string.Join(", ", s.OverlayExclude);
        FontBox.Text = s.FontFamily;
        StartupBox.IsChecked = Sys.StartsWithWindows;
        ShellBox.IsChecked = Sys.IsRegisteredShell;
        StartupBox.IsEnabled = ShellBox.IsChecked != true;
        ModeText.Text = App.ShellMode ? "Running as the Windows shell." : "Running on top of Windows Explorer.";
        _loading = false;

        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); Apply(); };
        Closed += (_, _) => { if (_saveDebounce.IsEnabled) Apply(); };
    }

    void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    void Apply()
    {
        _saveDebounce.Stop();
        var s = Settings.Current;
        s.DockPosition = PosLeft.IsChecked == true ? "Left" : PosRight.IsChecked == true ? "Right" : "Bottom";
        s.DockIconSize = Math.Round(SizeSlider.Value);
        s.DockMagnify = MagnifyBox.IsChecked == true;
        s.DockMagnification = Math.Round(MagnifySlider.Value, 2);
        s.DockAutoHide = AutoHideBox.IsChecked == true;
        s.HideWindowsTaskbar = HideTaskbarBox.IsChecked == true;
        s.TrafficLightOverlay = OverlayBox.IsChecked == true;
        s.OverlayExclude = ExcludeBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (FontBox.Text.Trim().Length > 0) s.FontFamily = FontBox.Text.Trim();
        Settings.Save();
    }

    void Startup_Click(object sender, RoutedEventArgs e) => Sys.StartsWithWindows = StartupBox.IsChecked == true;

    void Shell_Click(object sender, RoutedEventArgs e)
    {
        if (ShellBox.IsChecked == true)
        {
            var ok = MessageBox.Show(
                "UnixLauncher will replace Windows Explorer as your shell the next time you sign in (this user only).\n\n" +
                "If anything goes wrong: press Ctrl+Alt+Del › Task Manager › Run new task › explorer.exe, then untick this option.\n\n" +
                "Don't move or delete UnixLauncher.exe while this is on.\n\nContinue?",
                "Use as Windows shell", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.OK) { ShellBox.IsChecked = false; return; }
        }
        Sys.SetRegisteredShell(ShellBox.IsChecked == true);
        StartupBox.IsEnabled = ShellBox.IsChecked != true;
        ModeText.Text = "Takes effect the next time you sign in.";
    }

    void OpenIcons_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Icons.UserDir);
        var readme = Path.Combine(AppContext.BaseDirectory, "icons", "README.md");
        var copy = Path.Combine(Icons.UserDir, "README.md");
        if (File.Exists(readme) && !File.Exists(copy)) File.Copy(readme, copy);
        Sys.Launch(Icons.UserDir);
    }

    void ReloadIcons_Click(object sender, RoutedEventArgs e) => Settings.Save(); // Settings.Changed clears the icon cache and rebuilds
}
