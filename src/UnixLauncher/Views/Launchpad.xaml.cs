using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace UnixLauncher.Views;

/// <summary>Full-screen grid of every installed app, with search.</summary>
public partial class Launchpad : Popover
{
    protected override bool UseGlass => false; // full-screen: the plain rectangular blur fits it

    public Launchpad()
    {
        InitializeComponent();
        SearchIcon.Content = UI.Icon("search", "", 13);
        SourceInitialized += (_, _) => Native.EnableBlur(UI.Hwnd(this));
        Activated += (_, _) => Keyboard.Focus(Query);
        AppIndex.Loaded += Filter;
    }

    protected override void OnOpening()
    {
        Left = 0; Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Query.Text = "";
        Filter();
    }

    // macOS: the grid settles in from slightly larger while the backdrop fades in, and zooms back out on close.
    protected override void AnimateIn(FrameworkElement root) => Zoom(1.08, 1, 0, 1, EasingMode.EaseOut, null);
    protected override void AnimateOut(FrameworkElement root, Action done) => Zoom(1, 1.08, 1, 0, EasingMode.EaseIn, done);

    void Zoom(double fromScale, double toScale, double fromOpacity, double toOpacity, EasingMode easing, Action? done)
    {
        if (!UI.Motion) { BeginAnimation(OpacityProperty, null); done?.Invoke(); return; }
        var dur = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = easing };
        var zoom = new ScaleTransform();
        Page.RenderTransform = zoom;
        zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fromScale, toScale, dur) { EasingFunction = ease });
        zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(fromScale, toScale, dur) { EasingFunction = ease });
        var fade = new DoubleAnimation(fromOpacity, toOpacity, dur);
        if (done != null) fade.Completed += (_, _) => { done(); BeginAnimation(OpacityProperty, null); };
        BeginAnimation(OpacityProperty, fade);
    }

    void Filter()
    {
        string q = Query.Text.Trim();
        AppGrid.ItemsSource = q.Length == 0 ? AppIndex.Apps : AppSearch.Filter(AppIndex.Apps, a => a.Name, q, int.MaxValue);
    }

    void Query_Changed(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
        Filter();
    }

    void Query_Keys(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && AppGrid.Items.Count > 0) Launch((AppEntry)AppGrid.Items[0]);
    }

    void Launch(AppEntry app)
    {
        Dismiss(); // zooms away while the app starts, as on macOS
        Sys.Launch(app.Launch);
    }

    void App_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: AppEntry app }) Launch(app); }

    void App_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: AppEntry app }) return;
        e.Handled = true;
        bool pinned = Settings.Current.Pinned.Any(p => string.Equals(p.Launch, app.Launch, StringComparison.OrdinalIgnoreCase));
        var menu = UI.Menu(
            UI.Item("Open", () => Launch(app)),
            UI.Item(pinned ? "In Dock" : "Add to Dock", () => { Settings.Current.Pinned.Add(app.ToPin()); Settings.Save(); }, enabled: !pinned));
        menu.IsOpen = true;
    }

    void Background_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender) Dismiss(); // empty space, like macOS
    }
}
