using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        Left = SystemParameters.PrimaryScreenWidth - Width - 4;
        Top = 26;
        Height = Math.Max(400, wa.Bottom - Top);
        DateText.Text = DateTime.Now.ToString("dddd d MMMM");
        UpdateStatus();
    }

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

        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
            ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize, Width = 372, SizeToContent = SizeToContent.Height,
            Left = SystemParameters.PrimaryScreenWidth - 376, Top = 30, FontSize = 13,
            Content = new Border
            {
                Background = (Brush)Application.Current.Resources["PanelBg"], BorderBrush = (Brush)Application.Current.Resources["Line"],
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(8), Child = stack,
            },
        };
        w.SetResourceReference(Control.FontFamilyProperty, "UiFont");
        w.SourceInitialized += (_, _) => Native.SetToolWindow(UI.Hwnd(w), true);
        w.MouseLeftButtonUp += (_, _) => { w.Close(); App.NotificationCentre.Open(); };
        w.Closed += (_, _) => { if (_current == w) _current = null; };
        _current = w;
        w.Show();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => { timer.Stop(); w.Close(); };
        timer.Start();
    }
}
