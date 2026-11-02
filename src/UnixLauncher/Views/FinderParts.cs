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

/// <summary>
/// macOS Get Info: a small panel per item. Header with icon, name, size and date; sections that fold away (General,
/// Name &amp; Extension); everything else Windows knows is one button away in its Properties dialog.
/// </summary>
static class InfoWindow
{
    public static void Show(Window owner, string path, FileItem? item)
    {
        bool dir = Directory.Exists(path);
        FileSystemInfo info = dir ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists) return;
        var sub = (Brush)Application.Current.Resources["SubText"];
        string name = Path.GetFileName(path.TrimEnd('\\'));
        if (name.Length == 0) name = path;
        long bytes = dir ? -1 : ((FileInfo)info).Length;

        var root = new StackPanel { Width = 300, Margin = new Thickness(0, -6, 0, 0) };

        // Header: icon, name, size, modified
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var icon = new Image { Width = 40, Height = 40, Margin = new Thickness(0, 0, 10, 0), Source = item?.Icon ?? Icons.ShellUncached(path) };
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        var sizeText = new TextBlock { FontWeight = FontWeights.SemiBold, Text = dir ? "" : FileFormat.Size(bytes), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(sizeText, Dock.Right);
        head.Children.Add(sizeText);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        titles.Children.Add(new TextBlock { Text = "Modified: " + FileFormat.Date(info.LastWriteTime, DateTime.Now), Foreground = sub, FontSize = 11 });
        head.Children.Add(titles);
        root.Children.Add(head);

        // General
        var general = new Grid { Margin = new Thickness(18, 2, 0, 6) };
        general.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        general.ColumnDefinitions.Add(new ColumnDefinition());
        void Row(string label, UIElement value)
        {
            int r = general.RowDefinitions.Count;
            general.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label + ":", Foreground = sub, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 1, 8, 1) };
            Grid.SetRow(l, r);
            Grid.SetRow(value, r); Grid.SetColumn(value, 1);
            general.Children.Add(l); general.Children.Add(value);
        }
        TextBlock Val(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
        string Full(DateTime d) => $"{d:d MMMM yyyy} at {d:t}";
        Row("Kind", Val(item?.Kind is { Length: > 0 } k ? k : dir ? "Folder" : "Document"));
        var sizeVal = Val(dir ? "Calculating…" : $"{bytes:N0} bytes ({FileFormat.Size(bytes)})");
        Row("Size", sizeVal);
        string parent = Path.GetDirectoryName(path.TrimEnd('\\')) ?? "";
        Row("Where", Val(parent.Replace("\\", " ▸ ").TrimEnd(' ', '▸')));
        Row("Created", Val(Full(info.CreationTime)));
        Row("Modified", Val(Full(info.LastWriteTime)));
        if (!dir)
        {
            var locked = new CheckBox { Content = "Locked", Foreground = (Brush)Application.Current.Resources["Text"], Margin = new Thickness(0, 6, 0, 0),
                                        IsChecked = info.Attributes.HasFlag(FileAttributes.ReadOnly) };
            locked.Click += (_, _) =>
            {
                try { info.Attributes = locked.IsChecked == true ? info.Attributes | FileAttributes.ReadOnly : info.Attributes & ~FileAttributes.ReadOnly; }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Get Info"); locked.IsChecked = !locked.IsChecked; }
            };
            Row("", locked);
        }
        root.Children.Add(Section("General:", general));

        // Name & Extension: edit to rename
        var nameBox = new TextBox { Text = name, Margin = new Thickness(18, 4, 0, 6), Padding = new Thickness(4, 3, 4, 3) };
        string current = path;
        void Commit()
        {
            string want = nameBox.Text.Trim();
            string now = Path.GetFileName(current.TrimEnd('\\'));
            if (want == now || want.Length == 0) { nameBox.Text = now; return; }
            if (FileNames.Problem(want) is { } problem) { MessageBox.Show(problem, "Get Info"); nameBox.Text = now; return; }
            string dest = Path.Combine(Path.GetDirectoryName(current.TrimEnd('\\'))!, want);
            try
            {
                if (string.Equals(want, now, StringComparison.OrdinalIgnoreCase)) // case-only change: via a temporary name
                {
                    string tmp = dest + ".renaming";
                    FinderWindow.MovePath(current, tmp, dir);
                    FinderWindow.MovePath(tmp, dest, dir);
                }
                else FinderWindow.MovePath(current, dest, dir);
                current = dest;
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Get Info"); nameBox.Text = now; }
        }
        nameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        nameBox.LostFocus += (_, _) => Commit();
        root.Children.Add(Section("Name & Extension:", nameBox));

        var more = new Button { Content = "More in Windows Properties…", Style = (Style)Application.Current.Resources["FlatButton"],
                                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(10, 4, 0, 0) };
        root.Children.Add(more);

        var w = new MacWindow { Title = name, SizeToContent = SizeToContent.Height, Width = 340, ShowInTaskbar = false, Content = root,
                                WindowStartupLocation = WindowStartupLocation.Manual };
        // Cascade beside the Finder window, as macOS places Info windows.
        w.Left = owner.Left + owner.ActualWidth - 40 + (InfoCount % 8) * 22;
        w.Top = owner.Top + 40 + (InfoCount++ % 8) * 22;
        more.Click += (_, _) => Native.SHObjectProperties(UI.Hwnd(w), 2, current, null);
        w.Show();

        if (dir) // folder sizes take a walk of the disk: fill in when known
            System.Threading.Tasks.Task.Run(() =>
            {
                long total = 0; int count = 0;
                try
                {
                    foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true }))
                    { total += f.Length; count++; }
                }
                catch { /* unreadable folder: show what was counted */ }
                return (total, count);
            }).ContinueWith(t => w.Dispatcher.BeginInvoke(() =>
            {
                sizeVal.Text = $"{t.Result.total:N0} bytes ({FileFormat.Size(t.Result.total)}) for {t.Result.count:N0} items";
                sizeText.Text = FileFormat.Size(t.Result.total);
            }));
    }

    static int InfoCount;

    /// <summary>A macOS Info section: a chevron header that folds its contents away.</summary>
    static UIElement Section(string title, UIElement body)
    {
        var chevron = new TextBlock { Text = "⌄", Width = 14, Margin = new Thickness(0, -3, 4, 0) };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(chevron);
        header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        var toggle = new Button { Content = header, Style = (Style)Application.Current.Resources["FlatButton"], HorizontalAlignment = HorizontalAlignment.Stretch,
                                  HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(2, 3, 2, 3) };
        toggle.Click += (_, _) =>
        {
            bool open = body.Visibility == Visibility.Visible;
            body.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            chevron.Text = open ? "›" : "⌄";
        };
        var section = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        section.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["Line"], Margin = new Thickness(0, 0, 0, 4) });
        section.Children.Add(toggle);
        section.Children.Add(body);
        return section;
    }
}
