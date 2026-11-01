using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace UnixLauncher.Views;

/// <summary>Full-screen grid of every installed app, with search.</summary>
public partial class Launchpad : Popover
{
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
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }

    void Filter()
    {
        string q = Query.Text.Trim();
        AppGrid.ItemsSource = q.Length == 0 ? AppIndex.Apps : AppSearch.Filter(AppIndex.Apps, a => a.Name, q, int.MaxValue);
    }

    void Query_Changed(object sender, TextChangedEventArgs e) => Filter();

    void Query_Keys(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && AppGrid.Items.Count > 0) Launch((AppEntry)AppGrid.Items[0]);
    }

    void Launch(AppEntry app)
    {
        Hide();
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
        if (e.OriginalSource == sender) Hide(); // empty space, like macOS
    }
}
