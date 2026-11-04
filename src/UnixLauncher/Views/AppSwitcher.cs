using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace UnixLauncher.Views;

/// <summary>
/// macOS ⌘Tab (Alt+Tab with Alt as ⌘): one icon per running app, most recently used first, on a strip of glass in the
/// middle of the screen. Tab / ⇧Tab / ← → move, letting go of ⌘ switches to that app's front window, Esc closes it.
/// A quick ⌘Tab tap switches to the previous app without showing anything, as on a Mac. The keys come from MacKeyFilter.
/// </summary>
static class AppSwitcher
{
    sealed record App(string Name, ImageSource? Icon, IntPtr Hwnd);

    static List<App>? _apps;
    static int _sel;
    static Window? _win;
    static StackPanel? _row;
    static readonly DispatcherTimer _showSoon = new() { Interval = TimeSpan.FromMilliseconds(140) };

    static AppSwitcher() => _showSoon.Tick += (_, _) => { _showSoon.Stop(); Show(); };

    public static void Step(int dir)
    {
        if (_apps == null)
        {
            _apps = Running();
            if (_apps.Count == 0) { _apps = null; return; }
            _sel = 0; // the app in front; the first step goes to the one before it
            _showSoon.Start();
        }
        _sel = (_sel + dir + _apps.Count) % _apps.Count;
        Highlight();
    }

    public static void Done()
    {
        var pick = _apps != null && _sel < _apps.Count ? _apps[_sel] : null;
        Cancel();
        if (pick != null) Native.Activate(pick.Hwnd);
    }

    public static void Cancel()
    {
        _showSoon.Stop();
        _apps = null;
        _win?.Close();
        _win = null;
    }

    /// <summary>Running apps in z-order (most recent first), one per app, with its front window. Finder counts, though it's ours.</summary>
    static List<App> Running()
    {
        var finder = FinderWindow.All.Select(UI.Hwnd).ToHashSet();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<App>();
        Native.EnumWindows((h, _) =>
        {
            if (finder.Contains(h))
            {
                if (seen.Add("::finder"))
                    apps.Add(new App("Finder", Icons.Override("finder") ?? Icons.Shell(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")), h));
            }
            else if (RunningApps.IsAppWindow(h))
            {
                var w = RunningApps.Describe(h);
                if (seen.Add(w.Key))
                {
                    string name = RunningApps.DisplayName(w);
                    apps.Add(new App(name, Icons.ForApp(name, w.Exe, RunningApps.IconPath(w)), h));
                }
            }
            return true;
        }, IntPtr.Zero);
        return apps;
    }

    static void Show()
    {
        if (_apps == null) return;
        double size = Math.Min(96, (SystemParameters.WorkArea.Width - 120) / _apps.Count - 24); // many apps: smaller icons, one row
        _row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var app in _apps)
            _row.Children.Add(new StackPanel
            {
                Margin = new Thickness(4),
                Children =
                {
                    new Border { CornerRadius = new CornerRadius(18), Padding = new Thickness(8), Child = new Image { Source = app.Icon, Width = size, Height = size } },
                    new TextBlock
                    {
                        Text = app.Name, Foreground = (Brush)Application.Current.Resources["Text"], FontSize = 12, Margin = new Thickness(0, 4, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = size + 40,
                    },
                },
            });
        var panel = new Border
        {
            Background = (Brush)Application.Current.Resources["MenuBg"], BorderBrush = (Brush)Application.Current.Resources["GlassRim"],
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(28), Padding = new Thickness(12, 12, 12, 8), Child = _row,
        };
        _win = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false,
            ShowActivated = false, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, Content = panel,
        };
        _win.SetResourceReference(Control.FontFamilyProperty, "UiFont");
        _win.SourceInitialized += (_, _) => Native.SetToolWindow(UI.Hwnd(_win), true);
        _win.ContentRendered += (_, _) => UI.GlassPanel(panel); // the blurred screen behind, once it has its place
        _win.SizeChanged += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            _win.Left = wa.Left + (wa.Width - _win.ActualWidth) / 2;
            _win.Top = wa.Top + (wa.Height - _win.ActualHeight) / 2;
        };
        Highlight();
        _win.Show();
    }

    /// <summary>The chosen app gets the soft square behind its icon and shows its name, as on macOS.</summary>
    static void Highlight()
    {
        if (_row == null || _apps == null || _win == null) return;
        for (int i = 0; i < _row.Children.Count; i++)
        {
            var cell = (StackPanel)_row.Children[i];
            ((Border)cell.Children[0]).Background = i == _sel ? (Brush)Application.Current.Resources["HoverBg"] : Brushes.Transparent;
            cell.Children[1].Visibility = i == _sel ? Visibility.Visible : Visibility.Hidden;
        }
    }
}
