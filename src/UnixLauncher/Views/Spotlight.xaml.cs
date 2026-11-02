using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UnixLauncher.Views;

public record SearchResult(string Title, string Subtitle, ImageSource? Icon, Action Run);

public partial class Spotlight : Popover
{
    public Spotlight()
    {
        InitializeComponent();
        SearchIcon.Content = UI.Icon("search", "", 20);
        Activated += (_, _) => Keyboard.Focus(Query);
    }

    protected override void OnOpening()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = SystemParameters.PrimaryScreenHeight * 0.2;
        Query.Text = "";
    }

    void Query_Changed(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
        string q = Query.Text.Trim();
        var list = new List<SearchResult>();
        if (Calc.TryEvaluate(q, out var value))
            list.Add(new($"= {value}", "Copy result", null, () => Clipboard.SetText(value)));
        foreach (var app in AppSearch.Filter(AppIndex.Apps, a => a.Name, q, 8))
            list.Add(new(app.Name, "Application", app.Icon, () => Sys.Launch(app.Launch)));
        if (q.Length > 0)
        {
            string enc = Uri.EscapeDataString(q);
            list.Add(new($"Search in Finder for “{q}”", "Finder", null, () => FinderWindow.Open().StartSearch(q)));
            list.Add(new($"Search the web for “{q}”", "Browser", null, () => Sys.Launch("https://www.google.com/search?q=" + enc)));
        }
        Results.ItemsSource = list;
        Results.SelectedIndex = list.Count > 0 ? 0 : -1;
        Results.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Query_Keys(object sender, KeyEventArgs e)
    {
        int n = Results.Items.Count;
        switch (e.Key)
        {
            case Key.Down when n > 0: Results.SelectedIndex = (Results.SelectedIndex + 1) % n; e.Handled = true; break;
            case Key.Up when n > 0: Results.SelectedIndex = (Results.SelectedIndex - 1 + n) % n; e.Handled = true; break;
            case Key.Enter: Run(Results.SelectedItem as SearchResult); e.Handled = true; break;
        }
        if (Results.SelectedItem != null) Results.ScrollIntoView(Results.SelectedItem);
    }

    void Results_Click(object sender, MouseButtonEventArgs e) =>
        Run((e.OriginalSource as FrameworkElement)?.DataContext as SearchResult);

    void Run(SearchResult? r)
    {
        if (r == null) return;
        Hide();
        r.Run();
    }
}
