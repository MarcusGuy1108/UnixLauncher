using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UnixLauncher.Views;

/// <summary>Space-bar preview of the selected file, like macOS Quick Look. Follows the Finder selection while open.</summary>
public class QuickLook : MacWindow
{
    static QuickLook? _open;
    static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".json", ".xml", ".csv", ".ini", ".cfg", ".yaml", ".yml", ".toml", ".html", ".css", ".js", ".ts",
        ".cs", ".py", ".ps1", ".bat", ".cmd", ".sh", ".sql", ".java", ".c", ".cpp", ".h", ".go", ".rs",
    };

    public static bool IsOpen => _open != null;

    public static void Toggle(Window owner, string? path)
    {
        if (_open != null) { _open.Close(); return; }
        if (path == null) return;
        _open = new QuickLook { Owner = owner, ShowActivated = false }; // Finder keeps focus, so arrow keys still move the selection
        _open.Closed += (_, _) => _open = null;
        _open.Load(path);
        _open.Show();
    }

    public static void Follow(string? path)
    {
        if (_open != null && path != null) _open.Load(path);
    }

    QuickLook()
    {
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        PreviewKeyDown += (_, e) => { if (e.Key is Key.Space or Key.Escape) { Close(); e.Handled = true; } };
    }

    void Load(string path)
    {
        Title = Path.GetFileName(path.TrimEnd('\\'));
        Content = Preview(path);
    }

    static FrameworkElement Preview(string path)
    {
        string ext = Path.GetExtension(path);
        var text = (Brush)Application.Current.Resources["Text"];
        try
        {
            if (FinderWindow.PictureExts.Contains(ext) && File.Exists(path))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
                bmp.DecodePixelWidth = 1600;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return new Image { Source = bmp, MaxWidth = 900, MaxHeight = 640, Stretch = Stretch.Uniform };
            }
            if (TextExts.Contains(ext) && new FileInfo(path).Length < 2_000_000)
                return new TextBox
                {
                    Text = File.ReadAllText(path), IsReadOnly = true, Width = 720, Height = 520, FontSize = 12,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"), Background = Brushes.Transparent, Foreground = text,
                    BorderThickness = new Thickness(0), TextWrapping = TextWrapping.NoWrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                };
        }
        catch (Exception ex) { App.Log(ex.Message); } // unreadable: fall through to the summary card

        // Anything else: large icon or thumbnail plus the basics, as Quick Look shows for files it can't render.
        bool dir = Directory.Exists(path);
        var card = new StackPanel { Width = 300 };
        card.Children.Add(new Image { Source = Icons.ShellUncached(path, thumbnail: true, size: 256), Width = 160, Height = 160, Margin = new Thickness(0, 0, 0, 12) });
        card.Children.Add(new TextBlock { Text = Path.GetFileName(path.TrimEnd('\\')), FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        try
        {
            string size = dir ? "Folder" : FileFormat.Size(new FileInfo(path).Length);
            card.Children.Add(new TextBlock
            {
                Text = $"{size} · Modified {FileFormat.Date(File.GetLastWriteTime(path), DateTime.Now)}",
                Foreground = (Brush)Application.Current.Resources["SubText"], TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            });
        }
        catch { /* file vanished */ }
        return card;
    }
}

/// <summary>Small dialog asking for a name (Rename / New Folder).</summary>
static class Prompt
{
    public static string? Ask(Window owner, string title, string initial, bool selectAll)
    {
        var box = new TextBox { Text = initial, Width = 320, Padding = new Thickness(4, 3, 4, 3) };
        var flat = (Style)Application.Current.Resources["FlatButton"];
        var ok = new Button { Content = "OK", IsDefault = true, Style = flat, FontWeight = FontWeights.SemiBold };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Style = flat, Margin = new Thickness(0, 0, 8, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var content = new StackPanel();
        content.Children.Add(box);
        content.Children.Add(buttons);
        var w = new MacWindow
        {
            Title = title, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, AnimateClose = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = content,
        };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) =>
        {
            box.Focus();
            int dot = selectAll ? -1 : initial.LastIndexOf('.'); // Finder selects the name but not the extension
            box.Select(0, dot > 0 ? dot : initial.Length);
        };
        return w.ShowDialog() == true ? box.Text.Trim() : null;
    }
}
