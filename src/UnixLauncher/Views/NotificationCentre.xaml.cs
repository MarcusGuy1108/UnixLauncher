using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace UnixLauncher.Views;

public partial class NotificationCentre : Popover
{
    public NotificationCentre()
    {
        InitializeComponent();
        List.ItemsSource = Notifications.Items;
        Notifications.Changed += UpdateStatus;
        UpdateStatus();
    }

    protected override void OnOpening()
    {
        var wa = SystemParameters.WorkArea;
        Left = SystemParameters.PrimaryScreenWidth - Width - 12;
        Top = 34; // just under the menu bar
        Height = Math.Max(400, wa.Bottom - Top - 8);
        DateText.Text = DateTime.Now.ToString("dddd d MMMM");
        BuildCalendar();
        UpdateStatus();
    }

    void BuildCalendar()
    {
        var today = DateTime.Today;
        var fmt = CultureInfo.CurrentCulture.DateTimeFormat;
        MonthText.Text = today.ToString("MMMM yyyy").ToUpper();
        var sub = (Brush)FindResource("SubText");
        DayNames.Children.Clear();
        for (int i = 0; i < 7; i++)
            DayNames.Children.Add(Cell(fmt.ShortestDayNames[((int)fmt.FirstDayOfWeek + i) % 7], sub, 10, false));
        Days.Children.Clear();
        foreach (var day in CalendarMath.Cells(today.Year, today.Month, fmt.FirstDayOfWeek))
            Days.Children.Add(Cell(day?.ToString() ?? "", (Brush)FindResource("Text"), 12, day == today.Day));
    }

    UIElement Cell(string text, Brush fg, double size, bool today) => new Border
    {
        Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(1),
        Background = today ? (Brush)FindResource("Accent") : Brushes.Transparent,
        Child = new TextBlock
        {
            Text = text, FontSize = size, Foreground = fg, FontWeight = today ? FontWeights.SemiBold : FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    void UpdateStatus()
    {
        StatusText.Text = Notifications.Status.Length > 0 ? Notifications.Status
                        : Notifications.Items.Count == 0 ? "No Notifications" : "";
        StatusText.Visibility = StatusText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ClearAll_Click(object sender, RoutedEventArgs e) => Notifications.ClearAll();
    void Dismiss_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: NotificationItem n }) Notifications.Remove(n); }
}

/// <summary>Top-right banner for a new notification (used only when we are the shell; Explorer shows its own).</summary>
static class Banner
{
    static Window? _current;

    public static void Show(NotificationItem n)
    {
        _current?.Close(); // one banner at a time
        var text = (Brush)Application.Current.Resources["Text"];
        var sub = (Brush)Application.Current.Resources["SubText"];
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = n.App, Foreground = sub, FontSize = 11 });
        stack.Children.Add(new TextBlock { Text = n.Title, Foreground = text, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        if (n.Body.Length > 0) stack.Children.Add(new TextBlock { Text = n.Body, Foreground = text, TextWrapping = TextWrapping.Wrap, MaxHeight = 54, TextTrimming = TextTrimming.CharacterEllipsis });

        var panel = new Border
        {
            Background = (Brush)Application.Current.Resources["PanelBg"], BorderBrush = (Brush)Application.Current.Resources["Line"],
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10, 14, 12), Child = stack,
        };
        var w = new Window
        {
            Topmost = true, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize, Width = 356,
            SizeToContent = SizeToContent.Height, Left = SystemParameters.PrimaryScreenWidth - 356 - 12, Top = 34, FontSize = 13, Content = panel,
        };
        if (Glass.Setup(w)) Glass.Style(panel);
        w.SetResourceReference(Control.FontFamilyProperty, "UiFont");
        w.SourceInitialized += (_, _) => Native.SetToolWindow(UI.Hwnd(w), true);
        w.MouseLeftButtonUp += (_, _) => { w.Close(); App.NotificationCentre.Open(); };
        w.Closed += (_, _) => { if (_current == w) _current = null; };
        _current = w;
        w.Show();
        // Slide in from the right edge like a macOS banner, and back out when it times out.
        if (UI.Motion)
            w.BeginAnimation(Window.LeftProperty, new DoubleAnimation(SystemParameters.PrimaryScreenWidth, w.Left, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!UI.Motion) { w.Close(); return; }
            var away = new DoubleAnimation(SystemParameters.PrimaryScreenWidth, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            away.Completed += (_, _) => w.Close();
            w.BeginAnimation(Window.LeftProperty, away);
        };
        timer.Start();
    }
}
