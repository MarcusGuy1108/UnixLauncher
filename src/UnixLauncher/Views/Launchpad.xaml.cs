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
    protected override bool UseGlass => false; // a field and a card, each its own glass

    public Launchpad()
    {
        InitializeComponent();
        UI.FloatingGlass(this);
        SearchIcon.Content = UI.Icon("search", "", 16);
        Activated += (_, _) => Keyboard.Focus(Query);
        AppIndex.Loaded += Filter;
    }

    protected override void OnOpening()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = SystemParameters.PrimaryScreenHeight * 0.14;
        Query.Text = "";
        Filter();
    }

    // The glass follows the layout, not a scale animation: fade in and out.
    protected override void AnimateIn(FrameworkElement root) => UI.PopIn(root, from: 1);
    protected override void AnimateOut(FrameworkElement root, Action done) => UI.PopOut(root, done, to: 1);

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

}
