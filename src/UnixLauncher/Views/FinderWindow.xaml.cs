using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;

namespace UnixLauncher.Views;

public class FileItem : INotifyPropertyChanged
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsDir { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public string Kind { get; init; } = "";
    /// <summary>Extra line under the name in icon view (a drive's free space), as Finder shows for volumes.</summary>
    public string? Info { get; init; }

    /// <summary>Like Explorer, shortcuts show without their .lnk / .url extension.</summary>
    public string DisplayName => !IsDir && (Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || Name.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        ? System.IO.Path.GetFileNameWithoutExtension(Name) : Name;
    public string DateText => Modified == default ? "--" : FileFormat.Date(Modified, DateTime.Now);
    public string SizeText => IsDir ? "--" : FileFormat.Size(Size);

    ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A macOS Finder-style file browser.</summary>
public partial class FinderWindow : MacWindow
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly ConcurrentDictionary<string, string> Kinds = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, ImageSource?> TypeIcons = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly HashSet<string> PictureExts = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".ico" };
    // Files whose icon is their own (programs, shortcuts), not their type's.
    static readonly HashSet<string> OwnIconExts = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".lnk", ".url", ".ico", ".msc", ".cpl", ".scr", ".appref-ms" };

    // Locations that aren't folders on disk.
    public const string ThisPcPath = "::thispc", TrashPath = "::trash";
    string _current = Home;
    bool Virtual => _current.StartsWith("::");
    readonly Stack<string> _back = new(), _forward = new();
    List<FileItem> _all = new();
    string _sortBy = "Name";
    bool _desc, _listView, _searching, _suppressSearch;
    string? _error;
    int _generation; // bumped on every listing so a stale icon loader stops
    FileSystemWatcher? _watcher;
    CancellationTokenSource? _searchCts;
    readonly DispatcherTimer _reload = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly List<(string path, Button button)> _places = new();
    Point _pressAt;
    bool _pressedOnItem;
    ListBoxItem? _deferredSelect;

    public static IEnumerable<FinderWindow> All => Application.Current.Windows.OfType<FinderWindow>();

    public static FinderWindow Open(string? folder = null)
    {
        var w = new FinderWindow(folder ?? Home);
        w.Show();
        UI.BringToFront(w);
        return w;
    }

    /// <summary>"Show in Finder": open the item's folder with the item selected.</summary>
    public static void Reveal(string path)
    {
        string? dir = System.IO.Path.GetDirectoryName(path.TrimEnd('\\'));
        if (dir == null) { Open(path); return; }
        Open(dir).Select(path);
    }

    FinderWindow(string folder)
    {
        UnifiedToolbar = true;
        InitializeComponent();
        if (All.LastOrDefault(w => w != this) is { } prev) // cascade new windows
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = prev.Left + 26;
            Top = prev.Top + 26;
        }
        BackBtn.Content = UI.Icon("", "", 12);
        ForwardBtn.Content = UI.Icon("", "", 12);
        IconsBtn.Content = UI.Icon("", "", 12);
        ListBtn.Content = UI.Icon("", "", 12);
        ActionBtn.Content = UI.Icon("", "", 12);
        SearchIcon.Content = UI.Icon("search", "", 12);

        _listView = Settings.Current.FinderView == "List";
        ApplyView();
        _reload.Tick += (_, _) => { _reload.Stop(); if (!_searching) Reload(); };
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); RunSearch(); };
        PreviewKeyDown += OnKeys;
        PreviewMouseLeftButtonUp += (_, _) => ApplyDeferredSelect();
        Activated += (_, _) => // picks up drives plugged in since, and anything trashed elsewhere
        {
            BuildSidebar();
            if (Virtual && !_searching) Reload();
        };
        Closed += (_, _) => { _watcher?.Dispose(); _searchCts?.Cancel(); _generation++; };
        BuildSidebar();
        Navigate(folder, addHistory: false);
    }

    // ---------- Navigation ----------
    public void Navigate(string dir, bool addHistory = true)
    {
        if (dir == "::apps") { App.Launchpad.Open(); return; }
        string full = dir;
        if (dir is not (ThisPcPath or TrashPath))
        {
            try { full = System.IO.Path.GetFullPath(dir); } catch { return; }
            if (!Directory.Exists(full)) { MessageBox.Show(this, $"“{dir}” can't be found.", "Finder"); return; }
        }
        if (addHistory && !string.Equals(full, _current, StringComparison.OrdinalIgnoreCase)) { _back.Push(_current); _forward.Clear(); }
        _current = full;
        _searching = false;
        _searchCts?.Cancel();
        if (SearchBox.Text.Length > 0) { _suppressSearch = true; SearchBox.Text = ""; _suppressSearch = false; }
        Title = TitleText.Text = DisplayName(full);
        Watch(full);
        Reload(keepSelection: false);
        BuildPathBar();
        HighlightPlace();
        BackBtn.IsEnabled = _back.Count > 0;
        ForwardBtn.IsEnabled = _forward.Count > 0;
    }

    void Back() { if (_back.Count == 0) return; _forward.Push(_current); Navigate(_back.Pop(), addHistory: false); }
    void Forward() { if (_forward.Count == 0) return; _back.Push(_current); Navigate(_forward.Pop(), addHistory: false); }

    void Up()
    {
        if (Virtual) return;
        string child = _current;
        Navigate(Directory.GetParent(_current)?.FullName ?? ThisPcPath); // above a drive is This PC
        Select(child);
    }

    static string DisplayName(string path)
    {
        if (path == ThisPcPath) return "This PC";
        if (path == TrashPath) return "Recycle Bin";
        string? root = System.IO.Path.GetPathRoot(path);
        if (root != null && string.Equals(root.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            try { return DriveLabel(new DriveInfo(root)); } catch { return root; }
        }
        return System.IO.Path.GetFileName(path.TrimEnd('\\'));
    }

    static string DriveLabel(DriveInfo d)
    {
        string label = "";
        try { label = d.VolumeLabel; } catch { /* not ready / no access */ }
        if (label.Length == 0)
            label = d.DriveType switch { DriveType.Removable => "USB Drive", DriveType.Network => "Network Drive", DriveType.CDRom => "CD Drive", _ => "Local Disk" };
        return $"{label} ({d.Name.TrimEnd('\\')})";
    }

    // ---------- Listing ----------
    void Reload(bool keepSelection = true)
    {
        var keep = keepSelection ? Selected().Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var old = _all.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        _error = null;
        try
        {
            _all = _current switch
            {
                ThisPcPath => DriveItems(),
                TrashPath => TrashItems(),
                _ => new DirectoryInfo(_current).EnumerateFileSystemInfos("*", new EnumerationOptions()).Select(ToItem).ToList(),
            };
        }
        catch (Exception ex) { _all = new(); _error = ex.Message; }
        foreach (var it in _all) // unchanged files keep their already-loaded icon
            if (old.TryGetValue(it.Path, out var o) && o.Modified == it.Modified) it.Icon = o.Icon;
        Present(keep, keepScroll: keepSelection);
    }

    static bool Ready(DriveInfo d)
    {
        try { return d.DriveType == DriveType.Network || d.IsReady; } // skips empty card readers / CD drives
        catch { return false; }
    }

    /// <summary>This PC: every drive, with free space where Finder shows a volume's capacity.</summary>
    static List<FileItem> DriveItems() => DriveInfo.GetDrives().Where(Ready).Select(d =>
    {
        long total = 0, free = 0;
        try { total = d.TotalSize; free = d.AvailableFreeSpace; } catch { /* network drive offline */ }
        return new FileItem
        {
            Path = d.RootDirectory.FullName, Name = DriveLabel(d), IsDir = true, Size = total,
            Kind = total > 0 ? $"{FileFormat.Size(free)} free of {FileFormat.Size(total)}" : "Drive",
            Info = total > 0 ? $"{FileFormat.Size(free)} free of {FileFormat.Size(total)}" : null,
        };
    }).ToList();

    /// <summary>Recycle Bin items through the shell (deleted files live under hidden $Recycle.Bin folders on each drive).</summary>
    static IEnumerable<dynamic> TrashShellItems()
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic items = shell.NameSpace(10).Items(); // 10 = ssfBITBUCKET
        int n = items.Count;
        for (int i = 0; i < n; i++) yield return items.Item(i);
    }

    static List<FileItem> TrashItems()
    {
        var list = new List<FileItem>();
        foreach (var item in TrashShellItems())
        {
            try { list.Add(TrashItem(item)); }
            catch (Exception ex) { App.Log($"Recycle Bin item skipped: {ex.Message}"); } // one unreadable item mustn't hide the rest
        }
        return list;
    }

    static FileItem TrashItem(dynamic item)
    {
        string path = item.Path, name = item.Name;
        string ext = System.IO.Path.GetExtension(path);
        if (ext.Length > 0 && !name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name += ext; // Explorer may hide extensions
        bool dir = Directory.Exists(path);
        string from = item.ExtendedProperty("System.Recycle.DeletedFrom") as string ?? "";
        DateTime when = item.ExtendedProperty("System.Recycle.DateDeleted") is DateTime d ? d : File.GetLastWriteTime(path);
        return new FileItem
        {
            Path = path, Name = name, IsDir = dir, Size = dir ? 0 : new FileInfo(path).Length, Modified = when,
            Kind = from.Length > 0 ? $"From {from}" : "Deleted item", // where Put Back will return it
        };
    }

    static FileItem ToItem(FileSystemInfo f)
    {
        bool dir = f is DirectoryInfo;
        return new FileItem
        {
            Path = f.FullName, Name = f.Name, IsDir = dir, Size = dir ? 0 : ((FileInfo)f).Length, Modified = f.LastWriteTime,
            Kind = dir ? "Folder" : Kinds.GetOrAdd(f.Extension, ext =>
                ext.Length == 0 ? "Document" : Native.TypeName("x" + ext) is { Length: > 0 } t ? t : ext.TrimStart('.').ToUpperInvariant() + " File"),
        };
    }

    void Present(HashSet<string>? select = null, bool keepScroll = false)
    {
        var scroll = FindScroll(Files);
        double offset = scroll?.VerticalOffset ?? 0;
        var list = Sorted(_all).ToList();
        Files.ItemsSource = list;
        if (select != null)
            foreach (var it in list.Where(i => select.Contains(i.Path))) Files.SelectedItems.Add(it);
        if (keepScroll && scroll != null) Dispatcher.BeginInvoke(() => scroll.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
        UpdateStatus();
        LoadIcons(list);
    }

    IEnumerable<FileItem> Sorted(IEnumerable<FileItem> items)
    {
        var cmp = NaturalComparer.Instance;
        var folders = items.OrderBy(i => !i.IsDir); // folders on top: the Windows habit (Finder: "Keep folders on top")
        var sorted = (_sortBy, _desc) switch
        {
            ("Date", false) => folders.ThenBy(i => i.Modified),
            ("Date", true) => folders.ThenByDescending(i => i.Modified),
            ("Size", false) => folders.ThenBy(i => i.Size),
            ("Size", true) => folders.ThenByDescending(i => i.Size),
            ("Kind", false) => folders.ThenBy(i => i.Kind, cmp),
            ("Kind", true) => folders.ThenByDescending(i => i.Kind, cmp),
            (_, true) => folders.ThenByDescending(i => i.DisplayName, cmp),
            _ => folders.ThenBy(i => i.DisplayName, cmp),
        };
        return sorted.ThenBy(i => i.DisplayName, cmp);
    }

    /// <summary>Icons and picture thumbnails load on a background thread so big folders open instantly.</summary>
    void LoadIcons(List<FileItem> list)
    {
        int gen = ++_generation;
        bool perItem = list.Count <= 1500; // thumbnails and per-folder icons cost a disk read each; beyond this, one icon per type
        var todo = list.Where(i => i.Icon == null).ToList();
        if (todo.Count == 0) return;
        var t = new Thread(() =>
        {
            foreach (var it in todo)
            {
                if (gen != Volatile.Read(ref _generation)) return;
                var img = IconFor(it, perItem);
                Dispatcher.BeginInvoke(() => it.Icon = img);
            }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    static ImageSource? IconFor(FileItem it, bool perItem)
    {
        string ext = System.IO.Path.GetExtension(it.Name);
        if (it.IsDir) return perItem ? Icons.ShellUncached(it.Path, size: 96) : TypeIcons.GetOrAdd("<folder>", _ => Icons.ShellUncached(it.Path, size: 96));
        if (perItem && PictureExts.Contains(ext)) return Icons.ShellUncached(it.Path, thumbnail: true, size: 96);
        if (OwnIconExts.Contains(ext)) return Icons.ShellUncached(it.Path, size: 96);
        return TypeIcons.GetOrAdd(ext, _ => Icons.ShellUncached(it.Path, size: 96));
    }

    void Watch(string dir)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (dir.StartsWith("::")) return; // This PC / Recycle Bin refresh on activation and after our own changes
        try
        {
            var w = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            void Changed() => Dispatcher.BeginInvoke(() => { _reload.Stop(); _reload.Start(); }); // coalesce bursts (e.g. a download in progress)
            w.Created += (_, _) => Changed();
            w.Deleted += (_, _) => Changed();
            w.Changed += (_, _) => Changed();
            w.Renamed += (_, _) => Changed();
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch { /* some locations can't be watched; F5 still refreshes */ }
    }

    List<FileItem> Selected() => Files.SelectedItems.Cast<FileItem>().ToList();

    public void Select(string path)
    {
        if (Files.ItemsSource is not List<FileItem> list) return;
        string full = System.IO.Path.GetFullPath(path).TrimEnd('\\');
        var it = list.FirstOrDefault(i => string.Equals(i.Path.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase));
        if (it == null) return;
        Files.SelectedItems.Clear();
        Files.SelectedItems.Add(it);
        Files.ScrollIntoView(it);
    }

    void UpdateStatus()
    {
        int n = Files.Items.Count, k = Files.SelectedItems.Count;
        string text = k > 0 ? $"{k} of {n} selected" : n == 1 ? "1 item" : $"{n} items";
        try
        {
            if (_current == TrashPath) text += $", {FileFormat.Size(_all.Sum(i => i.Size))}";
            else if (!Virtual && !_current.StartsWith(@"\\")) text += $", {FileFormat.Size(new DriveInfo(_current).AvailableFreeSpace)} available";
        }
        catch { /* drive went away */ }
        StatusText.Text = _error ?? text;
    }

    static ScrollViewer? FindScroll(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is ScrollViewer sv) return sv;
            if (FindScroll(c) is { } found) return found;
        }
        return null;
    }

    // ---------- Views & sorting ----------
    void ApplyView()
    {
        IconsBtn.IsChecked = !_listView;
        ListBtn.IsChecked = _listView;
        Files.ItemsPanel = (ItemsPanelTemplate)FindResource(_listView ? "ListPanel" : "IconPanel");
        Files.ItemTemplate = (DataTemplate)FindResource(_listView ? "ListItem" : "IconItem");
        Files.ItemContainerStyle = (Style)FindResource(_listView ? "RowContainer" : "IconContainer");
        ListHeader.Visibility = _listView ? Visibility.Visible : Visibility.Collapsed;
        UpdateHeaders();
    }

    void SetView(bool list)
    {
        _listView = list;
        ApplyView();
        Settings.Current.FinderView = list ? "List" : "Icons";
        Settings.Save(notify: false);
    }

    void SortBy(string column)
    {
        if (_sortBy == column) _desc = !_desc;
        else { _sortBy = column; _desc = column is "Date" or "Size"; } // newest / biggest first, as Finder does
        UpdateHeaders();
        Present(Selected().Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    void UpdateHeaders()
    {
        string Arrow(string c) => _sortBy == c ? (_desc ? "  ▾" : "  ▴") : "";
        NameHeader.Content = "Name" + Arrow("Name");
        DateHeader.Content = "Date Modified" + Arrow("Date");
        SizeHeader.Content = "Size" + Arrow("Size");
        KindHeader.Content = "Kind" + Arrow("Kind");
    }

    // ---------- Search (this folder and everything below it) ----------
    void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
        if (_suppressSearch) return;
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    public void StartSearch(string query)
    {
        SearchBox.Text = query;
        _searchDelay.Stop();
        RunSearch();
    }

    async void RunSearch()
    {
        string q = SearchBox.Text.Trim();
        _searchCts?.Cancel();
        if (q.Length == 0)
        {
            if (!_searching) return;
            _searching = false;
            TitleText.Text = DisplayName(_current);
            Reload(keepSelection: false);
            return;
        }
        var cts = _searchCts = new CancellationTokenSource();
        string root = Virtual ? Home : _current; // searching from This PC / Recycle Bin looks in your files
        _searching = true;
        TitleText.Text = $"Searching “{q}”";
        StatusText.Text = "Searching…";
        List<FileItem> found;
        try
        {
            // shortcut: plain name match by walking the disk, capped at 1000 hits; Windows Search's index would be faster for whole drives.
            found = await Task.Run(() =>
            {
                var list = new List<FileItem>();
                foreach (var f in new DirectoryInfo(root).EnumerateFileSystemInfos("*" + q + "*", new EnumerationOptions { RecurseSubdirectories = true }))
                {
                    if (cts.IsCancellationRequested || list.Count >= 1000) break;
                    list.Add(ToItem(f));
                }
                return list;
            });
        }
        catch (Exception ex) { App.Log(ex.Message); return; }
        if (cts.IsCancellationRequested) return;
        _all = found;
        _error = null;
        Present();
    }

    // ---------- Commands ----------
    void OpenSelected()
    {
        foreach (var it in Selected()) Open(it);
    }

    void Open(FileItem it)
    {
        if (it.IsDir) { Navigate(it.Path); return; }
        if (it.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ShortcutTarget(it.Path) is { } target && Directory.Exists(target))
        {
            Navigate(target);
            return;
        }
        Sys.Launch(it.Path);
    }

    static string? ShortcutTarget(string lnk)
    {
        try
        {
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            return sh.CreateShortcut(lnk).TargetPath;
        }
        catch { return null; }
    }

    static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    void NewFolder()
    {
        if (_searching || Virtual) return;
        string path = System.IO.Path.Combine(_current, FileNames.Unique("untitled folder", n => Exists(System.IO.Path.Combine(_current, n)), keepExtension: false));
        try { Directory.CreateDirectory(path); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Finder"); return; }
        Reload();
        Select(path);
        Rename();
    }

    void Rename()
    {
        if (Virtual || Selected() is not [var it]) return;
        var name = Prompt.Ask(this, "Rename", it.Name, selectAll: it.IsDir);
        if (name == null || name == it.Name) return;
        if (FileNames.Problem(name) is { } problem) { MessageBox.Show(this, problem, "Finder"); return; }
        string dest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(it.Path)!, name);
        bool caseOnly = string.Equals(name, it.Name, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && Exists(dest)) { MessageBox.Show(this, $"The name “{name}” is already taken. Please choose a different name.", "Finder"); return; }
        try
        {
            if (caseOnly) // Windows treats a case-only rename as "same name"; go via a temporary name
            {
                string tmp = it.Path + ".renaming-" + Guid.NewGuid().ToString("N")[..6];
                MovePath(it.Path, tmp, it.IsDir);
                MovePath(tmp, dest, it.IsDir);
            }
            else MovePath(it.Path, dest, it.IsDir);
            if (_searching) RunSearch(); else { Reload(); Select(dest); }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Finder"); }
    }

    static void MovePath(string from, string to, bool dir) { if (dir) Directory.Move(from, to); else File.Move(from, to); }

    void MoveToTrash()
    {
        if (_current == TrashPath) { DeleteNow(); return; }
        if (_current == ThisPcPath) return; // drives can't be trashed
        Recycle(Selected().Select(i => i.Path).ToList());
    }

    static void Recycle(IReadOnlyList<string> paths)
    {
        var items = paths.Select(p => (p, Directory.Exists(p))).ToList();
        if (items.Count == 0) return;
        RunFileOp(() =>
        {
            foreach (var (p, dir) in items)
            {
                // AllDialogs, not silent: on drives without a Recycle Bin, Windows asks before deleting permanently.
                if (dir) FileSystem.DeleteDirectory(p, UIOption.AllDialogs, RecycleOption.SendToRecycleBin);
                else FileSystem.DeleteFile(p, UIOption.AllDialogs, RecycleOption.SendToRecycleBin);
            }
        });
    }

    /// <summary>Recycle Bin actions go through the shell so Windows restores files to where they came from.</summary>
    void TrashVerb(string verb)
    {
        var paths = Selected().Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0) return;
        try
        {
            foreach (var item in TrashShellItems().ToList())
                if (paths.Contains((string)item.Path)) item.InvokeVerb(verb);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Finder"); }
        Reload(keepSelection: false);
    }

    void PutBack() => TrashVerb("undelete");

    void DeleteNow()
    {
        int n = Files.SelectedItems.Count;
        if (n == 0 || MessageBox.Show(this, $"Delete {(n == 1 ? "this item" : $"these {n} items")} immediately? You can't undo this.",
                "Finder", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        TrashVerb("delete");
    }

    void EmptyTrash()
    {
        Sys.EmptyTrash(); // Windows asks for confirmation
        if (_current == TrashPath) Reload(keepSelection: false);
    }

    void GetInfo()
    {
        if (Virtual && Files.SelectedItems.Count == 0) return;
        var paths = Selected().Select(i => i.Path).DefaultIfEmpty(_current);
        foreach (var p in paths) Native.SHObjectProperties(UI.Hwnd(this), 2, p, null); // SHOP_FILEPATH: Windows' Properties dialog
    }

    void ToggleQuickLook() => QuickLook.Toggle(this, Selected().FirstOrDefault()?.Path);

    void CopySelected(bool cut)
    {
        if (Virtual) return;
        var paths = Selected().Select(i => i.Path).ToArray();
        if (paths.Length == 0) return;
        var files = new StringCollection();
        files.AddRange(paths);
        var data = new DataObject();
        data.SetFileDropList(files);
        // Explorer's convention (2 = move, 5 = copy), so Finder and Explorer can paste each other's files.
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        try { Clipboard.SetDataObject(data, true); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Finder"); }
    }

    static bool CanPaste() { try { return Clipboard.ContainsFileDropList(); } catch { return false; } }

    void Paste()
    {
        if (_searching || Virtual || !CanPaste()) return;
        try
        {
            var files = Clipboard.GetFileDropList().Cast<string>().ToList();
            bool move = Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && (ms.ReadByte() & 2) != 0;
            Transfer(files, _current, move);
            if (move) Clipboard.Clear(); // a cut is used up once pasted, as in Explorer
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Finder"); }
    }

    /// <summary>Copy or move into a folder with Windows' own progress and "replace or skip" dialogs.</summary>
    static void Transfer(IReadOnlyList<string> sources, string destDir, bool move) => RunFileOp(() =>
    {
        foreach (var src in sources)
        {
            bool dir = Directory.Exists(src);
            string name = System.IO.Path.GetFileName(src.TrimEnd('\\'));
            if (name.Length == 0 || (!dir && !File.Exists(src))) continue;
            if (dir && FileNames.IsSameOrInside(destDir, src))
            {
                if (!string.Equals(destDir.TrimEnd('\\'), src.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    MessageBox.Show($"“{name}” can't be {(move ? "moved" : "copied")} into itself.", "Finder");
                continue;
            }
            string dest = System.IO.Path.Combine(destDir, name);
            if (string.Equals(System.IO.Path.GetDirectoryName(src.TrimEnd('\\')), destDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                if (move) continue; // already here
                dest = System.IO.Path.Combine(destDir, FileNames.CopyName(name, dir, n => Exists(System.IO.Path.Combine(destDir, n)))); // "name copy"
            }
            if (dir)
            {
                if (move) FileSystem.MoveDirectory(src, dest, UIOption.AllDialogs);
                else FileSystem.CopyDirectory(src, dest, UIOption.AllDialogs);
            }
            else if (move) FileSystem.MoveFile(src, dest, UIOption.AllDialogs);
            else FileSystem.CopyFile(src, dest, UIOption.AllDialogs);
        }
    });

    /// <summary>File operations run on their own thread so a long copy never freezes the dock and menu bar.</summary>
    static void RunFileOp(Action op)
    {
        var t = new Thread(() =>
        {
            try { op(); }
            catch (OperationCanceledException) { /* user pressed Cancel in Windows' dialog */ }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Finder"); }
        }); // a foreground thread: quitting the launcher waits for the copy to finish
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    // ---------- Menus ----------
    ContextMenu ItemMenu()
    {
        var sel = Selected();
        var first = sel[0];
        bool one = sel.Count == 1;
        if (_current == TrashPath)
            return UI.Menu(
                UI.Item("Put Back", PutBack),
                UI.Item("Delete Immediately…", DeleteNow, keys: "Delete"),
                null,
                UI.Item($"Quick Look “{first.DisplayName}”", ToggleQuickLook, keys: "Space"),
                UI.Item("Get Info", GetInfo, keys: "Alt+Enter"));
        if (_current == ThisPcPath)
            return UI.Menu(
                UI.Item("Open", OpenSelected, keys: "Enter"),
                UI.Item("Get Info", GetInfo, keys: "Alt+Enter"));
        var items = new List<MenuItem?>
        {
            UI.Item("Open", OpenSelected, keys: "Enter"),
            UI.Item("Open With…", () => Process.Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {first.Path}"), enabled: one && !first.IsDir),
            null,
            UI.Item("Move to Recycle Bin", MoveToTrash, keys: "Delete"),
            null,
            UI.Item("Get Info", GetInfo, keys: "Alt+Enter"),
            UI.Item("Rename…", Rename, enabled: one, keys: "F2"),
            UI.Item($"Quick Look “{first.DisplayName}”", ToggleQuickLook, keys: "Space"),
            null,
            UI.Item("Copy", () => CopySelected(false), keys: "Ctrl+C"),
            UI.Item("Cut", () => CopySelected(true), keys: "Ctrl+X"),
            null,
        };
        if (_searching) items.Add(UI.Item("Show in Enclosing Folder", () => { Navigate(System.IO.Path.GetDirectoryName(first.Path)!); Select(first.Path); }));
        items.Add(UI.Item("Show in File Explorer", () => Sys.ShowInExplorer(first.Path))); // everything else Windows offers lives there
        return UI.Menu(items.ToArray());
    }

    ContextMenu FolderMenu() => UI.Menu(
        _current == TrashPath ? UI.Item("Empty Recycle Bin…", EmptyTrash, enabled: Files.Items.Count > 0) : null,
        UI.Item("New Folder", NewFolder, enabled: !_searching && !Virtual, keys: "Ctrl+Shift+N"),
        UI.Item("Paste", Paste, enabled: !_searching && !Virtual && CanPaste(), keys: "Ctrl+V"),
        null,
        UI.Item("as Icons", () => SetView(false), isChecked: !_listView, keys: "Ctrl+1"),
        UI.Item("as List", () => SetView(true), isChecked: _listView, keys: "Ctrl+2"),
        null,
        UI.Item("Sort by Name", () => SortBy("Name"), isChecked: _sortBy == "Name"),
        UI.Item("Sort by Date Modified", () => SortBy("Date"), isChecked: _sortBy == "Date"),
        UI.Item("Sort by Size", () => SortBy("Size"), isChecked: _sortBy == "Size"),
        UI.Item("Sort by Kind", () => SortBy("Kind"), isChecked: _sortBy == "Kind"),
        null,
        UI.Item("Get Info", GetInfo),
        UI.Item("Open in File Explorer", () => Process.Start("explorer.exe",
            _current switch { ThisPcPath => "shell:MyComputerFolder", TrashPath => "shell:RecycleBinFolder", _ => $"\"{_current}\"" })));

    void ShowMenu(ContextMenu menu, UIElement? under = null)
    {
        menu.PlacementTarget = under ?? Files;
        menu.Placement = under == null ? PlacementMode.MousePoint : PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Finder's menus for the top menu bar; also shown when the desktop is focused, as on macOS.</summary>
    public static List<(string Title, Func<ContextMenu> Menu)> MenuBarMenus(FinderWindow? w)
    {
        static bool Sel(FinderWindow? w) => w != null && w.Files.SelectedItems.Count > 0;
        FinderWindow Target() => w is { IsLoaded: true } ? w : Open();
        return new()
        {
            ("File", () => UI.Menu(
                UI.Item("New Finder Window", () => Open(), keys: "Ctrl+N"),
                UI.Item("New Folder", () => Target().NewFolder(), keys: "Ctrl+Shift+N"),
                UI.Item("Open", () => w!.OpenSelected(), enabled: Sel(w), keys: "Enter"),
                UI.Item("Close Window", () => w!.Close(), enabled: w != null, keys: "Ctrl+W"),
                null,
                UI.Item("Get Info", () => w!.GetInfo(), enabled: w != null, keys: "Alt+Enter"),
                UI.Item("Rename…", () => w!.Rename(), enabled: Sel(w), keys: "F2"),
                null,
                UI.Item("Move to Recycle Bin", () => w!.MoveToTrash(), enabled: Sel(w), keys: "Delete"),
                null,
                UI.Item("Quick Look", () => w!.ToggleQuickLook(), enabled: Sel(w), keys: "Space"))),
            ("Edit", () => UI.Menu(
                UI.Item("Cut", () => w!.CopySelected(true), enabled: Sel(w), keys: "Ctrl+X"),
                UI.Item("Copy", () => w!.CopySelected(false), enabled: Sel(w), keys: "Ctrl+C"),
                UI.Item("Paste", () => w!.Paste(), enabled: w != null && CanPaste(), keys: "Ctrl+V"),
                UI.Item("Select All", () => w!.Files.SelectAll(), enabled: w != null, keys: "Ctrl+A"))),
            ("View", () => UI.Menu(
                UI.Item("as Icons", () => w!.SetView(false), enabled: w != null, isChecked: w is { _listView: false }, keys: "Ctrl+1"),
                UI.Item("as List", () => w!.SetView(true), enabled: w != null, isChecked: w is { _listView: true }, keys: "Ctrl+2"),
                null,
                UI.Item("Sort by Name", () => w!.SortBy("Name"), enabled: w != null, isChecked: w?._sortBy == "Name"),
                UI.Item("Sort by Date Modified", () => w!.SortBy("Date"), enabled: w != null, isChecked: w?._sortBy == "Date"),
                UI.Item("Sort by Size", () => w!.SortBy("Size"), enabled: w != null, isChecked: w?._sortBy == "Size"),
                UI.Item("Sort by Kind", () => w!.SortBy("Kind"), enabled: w != null, isChecked: w?._sortBy == "Kind"))),
            ("Go", () =>
            {
                var go = new List<MenuItem?>
                {
                    UI.Item("Back", () => w!.Back(), enabled: w?._back.Count > 0, keys: "Alt+Left"),
                    UI.Item("Forward", () => w!.Forward(), enabled: w?._forward.Count > 0, keys: "Alt+Right"),
                    UI.Item("Enclosing Folder", () => w!.Up(), enabled: w != null, keys: "Alt+Up"),
                    null,
                    UI.Item("Applications", () => App.Launchpad.Open()),
                    UI.Item("This PC", () => Target().Navigate(ThisPcPath)),
                };
                foreach (var (name, _, path) in Favourites()) go.Add(UI.Item(name, () => Target().Navigate(path)));
                go.Add(UI.Item("Recycle Bin", () => Target().Navigate(TrashPath)));
                return UI.Menu(go.ToArray());
            }),
            ("Window", () =>
            {
                var win = new List<MenuItem?>
                {
                    UI.Item("Minimise", () => w!.Minimise(), enabled: w != null),
                    UI.Item("Zoom", () => w!.Zoom(), enabled: w != null),
                    null,
                };
                foreach (var f in All) { var target = f; win.Add(UI.Item(f.Title, () => { target.WindowState = WindowState.Normal; target.Activate(); }, isChecked: f == w)); }
                return UI.Menu(win.ToArray());
            }),
        };
    }

    // ---------- Sidebar & path bar ----------
    static IEnumerable<(string name, string glyph, string path)> Favourites()
    {
        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        yield return ("Desktop", "", F(Environment.SpecialFolder.DesktopDirectory));
        yield return ("Documents", "", F(Environment.SpecialFolder.MyDocuments));
        // shortcut: assumes Downloads lives in the profile folder; a relocated one needs SHGetKnownFolderPath.
        yield return ("Downloads", "", System.IO.Path.Combine(Home, "Downloads"));
        yield return ("Pictures", "", F(Environment.SpecialFolder.MyPictures));
        yield return ("Music", "", F(Environment.SpecialFolder.MyMusic));
        yield return ("Videos", "", F(Environment.SpecialFolder.MyVideos));
        yield return (System.IO.Path.GetFileName(Home), "", Home);
        if (Environment.GetEnvironmentVariable("OneDrive") is { Length: > 0 } od && Directory.Exists(od)) yield return ("OneDrive", "", od);
    }

    void BuildSidebar()
    {
        Sidebar.Children.Clear();
        _places.Clear();
        AddHeader("Favourites");
        AddPlace("Applications", "", "::apps");
        foreach (var (name, glyph, path) in Favourites()) AddPlace(name, glyph, path);
        AddHeader("Locations");
        AddPlace("This PC", "\uE7F8", ThisPcPath);
        foreach (var d in DriveInfo.GetDrives().Where(Ready))
        {
            string glyph = d.DriveType switch { DriveType.Removable => "", DriveType.Network => "", _ => "" };
            AddPlace(DriveLabel(d), glyph, d.RootDirectory.FullName);
        }
        AddPlace("Recycle Bin", "", TrashPath);
        HighlightPlace();
    }

    void AddHeader(string text) => Sidebar.Children.Add(new TextBlock
    {
        Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("SubText"), Margin = new Thickness(8, 12, 0, 4),
    });

    void AddPlace(string name, string glyph, string path)
    {
        var icon = UI.Icon("", glyph, 14);
        if (icon is TextBlock tb) tb.Foreground = (Brush)FindResource("Accent"); // Finder's blue sidebar glyphs
        icon.Width = 18;
        icon.Margin = new Thickness(0, 0, 8, 0);
        var row = new DockPanel();
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Style = (Style)FindResource("SidebarItem"), Content = row, ToolTip = path.StartsWith("::") ? null : path };
        b.Click += (_, _) => Navigate(path);
        if (path == TrashPath) // drag to the Recycle Bin, as to the macOS Trash
        {
            b.AllowDrop = true;
            b.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            b.Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] f) Recycle(f); };
        }
        else if (!path.StartsWith("::")) // drop files on a sidebar place to copy / move them there
        {
            b.AllowDrop = true;
            b.DragOver += (_, e) => { e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] f ? DropEffect(e, f, path) : DragDropEffects.None; e.Handled = true; };
            b.Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] f) Transfer(f, path, DropEffect(e, f, path) == DragDropEffects.Move); };
        }
        Sidebar.Children.Add(b);
        _places.Add((path, b));
    }

    void HighlightPlace()
    {
        foreach (var (path, b) in _places)
            b.Background = string.Equals(path.TrimEnd('\\'), _current.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                ? (Brush)FindResource("CardBg") : Brushes.Transparent;
    }

    void BuildPathBar()
    {
        PathBar.Children.Clear();
        var chain = new List<string>();
        if (!Virtual) for (var d = new DirectoryInfo(_current); d != null; d = d.Parent) chain.Insert(0, d.FullName);
        if (_current == TrashPath) chain.Add(TrashPath);
        else chain.Insert(0, ThisPcPath); // like Finder's path bar starting at the computer
        for (int i = 0; i < chain.Count; i++)
        {
            if (i > 0) PathBar.Children.Add(new TextBlock { Text = "›", Foreground = (Brush)FindResource("SubText"), Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center });
            string target = chain[i];
            var b = new Button { Style = (Style)FindResource("FlatButton"), Padding = new Thickness(4, 0, 4, 0), FontSize = 11, Content = DisplayName(target) };
            b.Click += (_, _) => Navigate(target);
            PathBar.Children.Add(b);
        }
    }

    // ---------- Mouse ----------
    void DragArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) Zoom(); else DragMove();
    }

    void Back_Click(object sender, RoutedEventArgs e) => Back();
    void Forward_Click(object sender, RoutedEventArgs e) => Forward();
    void IconsView_Click(object sender, RoutedEventArgs e) => SetView(false);
    void ListView_Click(object sender, RoutedEventArgs e) => SetView(true);
    void Header_Click(object sender, RoutedEventArgs e) => SortBy((string)((Button)sender).Tag);
    void Action_Click(object sender, RoutedEventArgs e) => ShowMenu(Files.SelectedItems.Count > 0 ? ItemMenu() : FolderMenu(), ActionBtn);

    ListBoxItem? ItemAt(object originalSource) =>
        originalSource is DependencyObject d ? ItemsControl.ContainerFromElement(Files, d) as ListBoxItem : null;

    void Files_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(e.OriginalSource) != null) OpenSelected();
    }

    void Files_RightClick(object sender, MouseButtonEventArgs e)
    {
        bool onItem = ItemAt(e.OriginalSource) != null;
        if (!onItem) Files.UnselectAll();
        ShowMenu(onItem && Files.SelectedItems.Count > 0 ? ItemMenu() : FolderMenu());
        e.Handled = true;
    }

    void Files_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateStatus();
        QuickLook.Follow(Selected().FirstOrDefault()?.Path);
    }

    static bool InScrollBar(DependencyObject? d)
    {
        for (; d is Visual; d = VisualTreeHelper.GetParent(d)) if (d is ScrollBar) return true;
        return false;
    }

    void Files_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressAt = e.GetPosition(Files);
        var item = ItemAt(e.OriginalSource);
        _pressedOnItem = item != null;
        _deferredSelect = null;
        if (item == null) { if (!InScrollBar(e.OriginalSource as DependencyObject)) Files.UnselectAll(); return; } // click on empty space
        // Pressing on one of several selected items keeps the group selected, so all of them can be dragged.
        if (item.IsSelected && Files.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None && e.ClickCount == 1)
        {
            _deferredSelect = item;
            Files.Focus();
            e.Handled = true;
        }
    }

    void ApplyDeferredSelect()
    {
        if (_deferredSelect is not { } item) return;
        _deferredSelect = null;
        Files.SelectedItems.Clear();
        item.IsSelected = true; // a click without a drag selects just that item
    }

    void Files_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressedOnItem || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(Files) - _pressAt;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pressedOnItem = false;
        _deferredSelect = null;
        var paths = Selected().Select(i => i.Path).ToArray();
        if (paths.Length > 0)
            DragDrop.DoDragDrop(Files, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
    }

    string DropTarget(DragEventArgs e) => ItemAt(e.OriginalSource) is { DataContext: FileItem { IsDir: true } f } ? f.Path : _current;

    /// <summary>Explorer's rules: Ctrl copies, Shift moves, otherwise move on the same drive and copy across drives.</summary>
    static DragDropEffects DropEffect(DragEventArgs e, string[] files, string target)
    {
        DragDropEffects want;
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) want = DragDropEffects.Copy;
        else if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0) want = DragDropEffects.Move;
        else want = files.Length > 0 && string.Equals(System.IO.Path.GetPathRoot(files[0]), System.IO.Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase)
            ? DragDropEffects.Move : DragDropEffects.Copy;
        if ((e.AllowedEffects & want) != 0) return want;
        return (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    void Files_DragOver(object sender, DragEventArgs e)
    {
        string target = DropTarget(e);
        e.Effects = _searching || e.Data.GetData(DataFormats.FileDrop) is not string[] files || target == ThisPcPath ? DragDropEffects.None
                  : target == TrashPath ? DragDropEffects.Move
                  : DropEffect(e, files, target);
        e.Handled = true;
    }

    void Files_Drop(object sender, DragEventArgs e)
    {
        if (_searching || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        string target = DropTarget(e);
        if (target == ThisPcPath) return;
        if (target == TrashPath) { Recycle(files); e.Handled = true; return; }
        var effect = DropEffect(e, files, target);
        if (effect == DragDropEffects.None) return;
        Transfer(files, target, effect == DragDropEffects.Move);
        e.Effects = effect;
        e.Handled = true;
    }

    // ---------- Keyboard (Windows keys for Finder's shortcuts) ----------
    // ---------- Keyboard: Windows keys and macOS ones, with Alt standing in for ⌘ (it sits where ⌘ does) ----------
    void OnKeys(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), alt = mods.HasFlag(ModifierKeys.Alt), shift = mods.HasFlag(ModifierKeys.Shift);
        bool cmd = ctrl || alt, typing = SearchBox.IsKeyboardFocusWithin;
        Action? act = (key, cmd, shift) switch
        {
            // Window-level commands work everywhere, even while typing in the search box…
            (Key.F, true, _) => () => SearchBox.Focus(),
            (Key.N, true, true) => NewFolder,
            (Key.N, true, false) => () => Open(_current),
            (Key.W, true, _) => Close,
            (Key.D1, true, _) => () => SetView(false),
            (Key.D2, true, _) => () => SetView(true),
            (Key.F5, false, _) => () => { if (_searching) RunSearch(); else Reload(); },
            (Key.Escape, false, _) when typing => () => { SearchBox.Text = ""; Files.Focus(); },
            // …but while typing, every other key belongs to the text (Ctrl+Backspace deletes a word, not files).
            _ when typing => alt ? TextCommand(key) : null,  // ⌘C / ⌘V… in the search box
            (Key.Left, _, _) when alt => Back,                // Windows: Alt+← / Alt+→
            (Key.Right, _, _) when alt => Forward,
            (Key.OemOpenBrackets, true, _) => Back,           // macOS: ⌘[ / ⌘]
            (Key.OemCloseBrackets, true, _) => Forward,
            (Key.Up, true, _) => Up,                          // ⌘↑ enclosing folder
            (Key.Down, true, _) => OpenSelected,              // ⌘↓ open
            (Key.O, true, false) => OpenSelected,
            (Key.I, true, false) => GetInfo,
            (Key.Enter, _, _) when alt => GetInfo,            // Windows: Alt+Enter
            (Key.Back, true, true) => EmptyTrash,             // ⌘⇧⌫
            (Key.Back, true, false) => MoveToTrash,           // ⌘⌫
            (Key.G, true, true) => GoToFolder,                // ⌘⇧G
            (Key.H, true, true) => () => Navigate(Home),      // ⌘⇧H / ⌘⇧D / ⌘⇧C
            (Key.D, true, true) => () => Navigate(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            (Key.C, true, true) => () => Navigate(ThisPcPath),
            (Key.F2, false, _) => Rename,
            (Key.Escape, false, _) when QuickLook.IsOpen => ToggleQuickLook,
            (Key.Enter, false, _) => Settings.Current.MacShortcuts ? Rename : OpenSelected, // Return renames in macOS Finder
            (Key.Back, false, _) => Back,
            (Key.Delete, false, _) => MoveToTrash,
            (Key.Space, false, _) => ToggleQuickLook,
            (Key.C, true, false) => () => CopySelected(false),
            (Key.X, true, false) => () => CopySelected(true),
            (Key.V, true, false) => Paste,
            (Key.A, true, false) => () => Files.SelectAll(),
            _ => null,
        };
        if (act == null) return;
        act();
        e.Handled = true;
    }

    Action? TextCommand(Key key)
    {
        RoutedUICommand? command = key switch
        {
            Key.C => ApplicationCommands.Copy, Key.X => ApplicationCommands.Cut, Key.V => ApplicationCommands.Paste,
            Key.A => ApplicationCommands.SelectAll, Key.Z => ApplicationCommands.Undo, _ => null,
        };
        return command == null ? null : () => command.Execute(null, SearchBox);
    }

    /// <summary>⌘⇧G: type a path (~ and %VARIABLES% work).</summary>
    void GoToFolder()
    {
        var path = Prompt.Ask(this, "Go to Folder", Virtual ? Home : _current, selectAll: true);
        if (string.IsNullOrWhiteSpace(path)) return;
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (path == "~" || path.StartsWith("~\\") || path.StartsWith("~/")) path = Home + path[1..];
        Navigate(path);
    }
}
