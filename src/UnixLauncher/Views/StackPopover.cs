using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UnixLauncher.Views;

/// <summary>macOS dock "stack": clicking a folder in the dock (e.g. Downloads) shows its newest items in a grid.</summary>
public class StackPopover : Popover
{
    const int Columns = 4, MaxItems = 16;
    protected override Point AnimationOrigin => new(0.5, 1); // grows up out of the dock
    static StackPopover? _open;

    readonly string _folder, _edge;
    readonly FrameworkElement _anchor;

    /// <summary>Open the stack for a folder, or close it if it is already open (second click on the icon).</summary>
    public static void Toggle(string folder, FrameworkElement anchor, string edge)
    {
        bool same = _open?._folder == folder;
        _open?.Close();
        if (same) return;
        _open = new StackPopover(folder, anchor, edge);
        _open.Open();
    }

    StackPopover(string folder, FrameworkElement anchor, string edge)
    {
        (_folder, _anchor, _edge) = (folder, anchor, edge);
        Width = Columns * 88 + 26;
        SizeToContent = SizeToContent.Height;
        Foreground = (Brush)FindResource("Text");
        FontSize = 13;

        var grid = new WrapPanel();
        foreach (var path in Newest(folder)) grid.Children.Add(Tile(path));
        if (grid.Children.Count == 0)
            grid.Children.Add(new TextBlock { Text = "No items", Foreground = (Brush)FindResource("SubText"), Margin = new Thickness(8, 4, 8, 12) });

        var open = new Button { Content = "Open in Finder", Style = (Style)FindResource("FlatButton"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        open.Click += (_, _) => { Close(); FinderWindow.Open(folder); };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = Path.GetFileName(folder.TrimEnd('\\', '/')), FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(6, 0, 0, 8) });
        stack.Children.Add(grid);
        stack.Children.Add(open);
        Content = new Border
        {
            Background = (Brush)FindResource("PanelBg"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(12), Child = stack,
        };

        IsVisibleChanged += (_, _) => { if (!IsVisible) Dispatcher.BeginInvoke(Close); }; // close once the fade-out has hidden it
        Closed += (_, _) => { if (_open == this) _open = null; };
        SizeChanged += (_, _) => Place(); // the real height is only known once shown
    }

    /// <summary>Newest first by creation time, i.e. macOS's default "Date Added" order.</summary>
    static IEnumerable<string> Newest(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFileSystemInfos()
                .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderByDescending(f => f.CreationTime)
                .Take(MaxItems).Select(f => f.FullName).ToList();
        }
        catch (Exception ex) { App.Log(ex.Message); return Array.Empty<string>(); }
    }

    Button Tile(string path)
    {
        string name = Path.GetFileName(path);
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new Image { Source = Icons.ShellUncached(path), Width = 48, Height = 48 });
        panel.Children.Add(new TextBlock
        {
            Text = name, FontSize = 11, Width = 80, MaxHeight = 30, Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center,
        });
        var tile = new Button { Style = (Style)FindResource("FlatButton"), Width = 88, Height = 96, Padding = new Thickness(4), ToolTip = name, Content = panel };
        tile.Click += (_, _) => { Close(); Sys.Launch(path); };
        return tile;
    }

    protected override void OnOpening() => Place();

    /// <summary>Above the dock icon (or beside it for a side dock), kept on screen.</summary>
    void Place()
    {
        var content = (FrameworkElement)Content;
        if (ActualHeight == 0) content.Measure(new Size(Width, double.PositiveInfinity));
        double h = ActualHeight > 0 ? ActualHeight : content.DesiredSize.Height;
        double s = UI.Scale(_anchor);
        var tl = _anchor.PointToScreen(new Point(0, 0));
        double x = tl.X / s, y = tl.Y / s, aw = _anchor.ActualWidth, ah = _anchor.ActualHeight;
        var (left, top) = _edge switch
        {
            "Left" => (x + aw + 14, y + ah / 2 - h / 2),
            "Right" => (x - Width - 14, y + ah / 2 - h / 2),
            _ => (x + aw / 2 - Width / 2, y - h - 14),
        };
        Left = Math.Clamp(left, 8, Math.Max(8, SystemParameters.PrimaryScreenWidth - Width - 8));
        Top = Math.Clamp(top, 34, Math.Max(34, SystemParameters.PrimaryScreenHeight - h - 8));
    }
}
