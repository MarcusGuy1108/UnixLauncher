using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace UnixLauncher.Views;

public partial class DockWindow : Window
{
    const double Pad = 6, Gap = 4, EdgeMargin = 6, DotRoom = 8;
    const string PinFormat = "UnixLauncher.PinIndex";
    const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    class Entry
    {
        public DockPin? Pin;
        public string Key = "";          // grouping key for unpinned running apps
        public bool Trash;
        public List<WinInfo> Windows = new();
    }

    IntPtr _hwnd;
    readonly Window? _glass; // acrylic backdrop kept under the shelf (Windows 11 22H2+)
    List<WinInfo> _windows = new();
    string _signature = "";
    // Parallel lists describing the stack, in order. Visual is null for separators.
    readonly List<FrameworkElement?> _visuals = new();
    readonly List<double> _baseSizes = new();
    readonly List<bool> _scalable = new();
    readonly List<FrameworkElement> _pinSlots = new();
    Image? _trashImage;
    bool? _trashFull;

    bool _fullscreenHidden, _autoShown = true, _menuOpen;
    DateTime _lastNear = DateTime.Now;
    Point _pressAt;
    Entry? _pressed;

    static Settings S => Settings.Current;
    static string Edge => S.DockPosition;
    static bool Vertical => Edge is "Left" or "Right";
    static double IconSize => S.DockIconSize;
    static double Thickness => IconSize + Gap * 2 + Pad * 2 + DotRoom;

    public DockWindow()
    {
        InitializeComponent();
        if (Glass.Supported)
        {
            // This window spans the whole screen edge (room for magnified icons), so blurring it would blur
            // that entire strip. Instead a separate shelf-sized acrylic window sits directly underneath.
            _glass = new Window { ShowInTaskbar = false, ShowActivated = false, Topmost = true, ResizeMode = ResizeMode.NoResize, Title = "Dock Backdrop" };
            Glass.Setup(_glass);
            _glass.SourceInitialized += (_, _) => Native.SetToolWindow(UI.Hwnd(_glass), true);
            Glass.Style(ShelfBg);
            ShelfBg.CornerRadius = new CornerRadius(8); // same radius DWM gives the backdrop window
            ShelfBg.SizeChanged += (_, _) => SyncGlass();
        }
        SourceInitialized += (_, _) =>
        {
            _hwnd = UI.Hwnd(this);
            Native.SetToolWindow(_hwnd, true);
            Layout();
        };

        var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        poll.Tick += (_, _) => RefreshWindows();
        poll.Start();
        var hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        hover.Tick += (_, _) => AutoHideTick();
        hover.Start();

        Settings.Changed += () => { Icons.ClearCache(); _signature = ""; Layout(); RefreshWindows(); };
        AppIndex.Loaded += () => { _signature = ""; RefreshWindows(); };
        MouseMove += (_, e) => Magnify(e.GetPosition(Root));
        MouseLeave += (_, _) => Magnify(null);
        MouseEnter += (_, _) => RefreshTrash();
        DragOver += OnDragOver;
        Drop += OnDrop;
        RefreshWindows();
    }

    // ---------- Placement ----------
    public void Layout()
    {
        if (_hwnd == IntPtr.Zero) return;
        double s = UI.Scale(this);
        double maxScale = S.DockMagnify ? Math.Max(1, S.DockMagnification) : 1;
        int cross = (int)Math.Ceiling((IconSize * maxScale + Gap * 2 + Pad * 2 + DotRoom + EdgeMargin + 4) * s);
        int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
        var (x, y, w, h) = Edge switch
        {
            "Left" => (0, 0, cross, sh),
            "Right" => (sw - cross, 0, cross, sh),
            _ => (0, sh - cross, sw, cross),
        };
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);

        uint edge = Edge switch { "Left" => Native.ABE_LEFT, "Right" => Native.ABE_RIGHT, _ => Native.ABE_BOTTOM };
        if (S.DockAutoHide) WorkArea.Release(_hwnd);
        else WorkArea.Reserve(_hwnd, edge, (int)Math.Round((Thickness + EdgeMargin) * s));

        var toEdge = Edge == "Left" ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Shelf.HorizontalAlignment = Vertical ? toEdge : HorizontalAlignment.Center;
        Shelf.VerticalAlignment = Vertical ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        Shelf.Margin = Edge switch { "Left" => new Thickness(EdgeMargin, 0, 0, 0), "Right" => new Thickness(0, 0, EdgeMargin, 0), _ => new Thickness(0, 0, 0, EdgeMargin) };
        // The shelf background keeps its size while magnified icons grow out of it, as on macOS.
        ShelfBg.Width = Vertical ? Thickness : double.NaN;
        ShelfBg.Height = Vertical ? double.NaN : Thickness;
        ShelfBg.HorizontalAlignment = Vertical ? toEdge : HorizontalAlignment.Stretch;
        ShelfBg.VerticalAlignment = Vertical ? VerticalAlignment.Stretch : VerticalAlignment.Bottom;
        Items.Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal;
        Items.HorizontalAlignment = Vertical ? toEdge : HorizontalAlignment.Center;
        Items.VerticalAlignment = Vertical ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        Items.Margin = new Thickness(Pad);

        Slide.BeginAnimation(TranslateTransform.XProperty, null);
        Slide.BeginAnimation(TranslateTransform.YProperty, null);
        Slide.X = Slide.Y = 0;
        _autoShown = true;
        if (S.DockAutoHide) SetAutoShown(false);
        Dispatcher.InvokeAsync(SyncGlass, DispatcherPriority.Loaded); // after the new layout is measured
    }

    public void SetHiddenForFullscreen(bool hidden)
    {
        if (hidden == _fullscreenHidden) return;
        _fullscreenHidden = hidden;
        if (hidden) Hide(); else Show();
        SyncGlass();
    }

    /// <summary>Keep the acrylic backdrop exactly under the shelf, just below this window in z-order.</summary>
    void SyncGlass()
    {
        if (_glass == null) return;
        if (!IsVisible || !_autoShown || ShelfBg.ActualWidth == 0) { _glass.Hide(); return; }
        var a = ShelfBg.PointToScreen(new Point(0, 0));
        var b = ShelfBg.PointToScreen(new Point(ShelfBg.ActualWidth, ShelfBg.ActualHeight));
        _glass.Show();
        Native.SetWindowPos(UI.Hwnd(_glass), _hwnd, (int)a.X, (int)a.Y, (int)Math.Round(b.X - a.X), (int)Math.Round(b.Y - a.Y), Native.SWP_NOACTIVATE);
    }

    // ---------- Items ----------
    void RefreshWindows()
    {
        _windows = RunningApps.Enumerate();
        var pins = S.Pinned;
        var entries = pins.Select(p => new Entry { Pin = p, Windows = _windows.Where(w => Matches(p, w)).ToList() }).ToList();
        entries.AddRange(_windows.Where(w => !pins.Any(p => Matches(p, w)))
                                 .GroupBy(w => w.Key)
                                 .Select(g => new Entry { Key = g.Key, Windows = g.ToList() }));

        string sig = string.Join("|", entries.Select(e => (e.Pin?.Launch ?? e.Key) + (e.Windows.Count > 0 ? "*" : "")));
        if (sig == _signature) return;
        _signature = sig;
        Rebuild(entries, pins.Count);
    }

    static bool Matches(DockPin p, WinInfo w) => AppMatch.Matches(p.Exe, p.Aumid, w.Exe, w.Aumid);

    List<WinInfo> WindowsFor(Entry e) =>
        e.Pin != null ? _windows.Where(w => Matches(e.Pin, w)).ToList() : _windows.Where(w => w.Key == e.Key).ToList();

    void Rebuild(List<Entry> entries, int pinCount)
    {
        Items.Children.Clear();
        _visuals.Clear(); _baseSizes.Clear(); _scalable.Clear(); _pinSlots.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            if (i == pinCount) AddSeparator(); // pinned | running-but-not-pinned
            var slot = AddIcon(entries[i]);
            if (i < pinCount) _pinSlots.Add(slot);
        }
        AddSeparator();
        _trashFull = null;
        AddIcon(new Entry { Trash = true });
        RefreshTrash();
    }

    void AddSeparator()
    {
        var line = new Rectangle
        {
            Fill = (Brush)FindResource("Line"),
            Width = Vertical ? IconSize * 0.8 : 1, Height = Vertical ? 1 : IconSize * 0.8,
            Margin = Vertical ? new Thickness(0, Gap * 1.5, 0, Gap * 1.5) : new Thickness(Gap * 1.5, 0, Gap * 1.5, 0),
            VerticalAlignment = Vertical ? VerticalAlignment.Center : VerticalAlignment.Bottom,
        };
        if (!Vertical) line.Margin = new Thickness(Gap * 1.5, 0, Gap * 1.5, IconSize * 0.1 + Gap + DotRoom);
        Items.Children.Add(line);
        _visuals.Add(null); _baseSizes.Add(1 + Gap * 3); _scalable.Add(false);
    }

    FrameworkElement AddIcon(Entry e)
    {
        string name;
        ImageSource? icon;
        if (e.Trash) { name = "Recycle Bin"; icon = null; }
        else if (e.Pin is { Launch: "::launchpad" }) { name = e.Pin.Name; icon = Icons.Override("launchpad"); }
        else if (e.Pin != null) { name = e.Pin.Name; icon = Icons.ForApp(e.Pin.Name, e.Pin.Exe, e.Pin.Launch); }
        else { var w = e.Windows[0]; name = RunningApps.DisplayName(w); icon = Icons.ForApp(name, w.Exe, RunningApps.IconPath(w)); }

        FrameworkElement visual;
        if (icon != null || e.Trash)
        {
            var img = new Image { Source = icon, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            if (e.Trash) _trashImage = img;
            visual = img;
        }
        else
        {
            // No icon found: rounded tile with a glyph. Drop a PNG in icons/ to replace it.
            string glyph = e.Pin is { Launch: "::launchpad" } ? "" : "";
            visual = new Border
            {
                CornerRadius = new CornerRadius(IconSize * 0.22), Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                Child = new Viewbox { Margin = new Thickness(IconSize * 0.2), Child = UI.Icon("", glyph, 32) },
            };
        }
        visual.Width = visual.Height = IconSize;
        visual.RenderTransform = new TranslateTransform();

        var dot = new Ellipse
        {
            Width = 4, Height = 4, Margin = new Thickness(2), Fill = (Brush)FindResource("Text"),
            Visibility = e.Windows.Count > 0 ? Visibility.Visible : Visibility.Hidden,
        };
        var slot = new DockPanel { Margin = new Thickness(Gap), Background = Brushes.Transparent, ToolTip = name, LastChildFill = true };
        DockPanel.SetDock(dot, Edge switch { "Left" => System.Windows.Controls.Dock.Left, "Right" => System.Windows.Controls.Dock.Right, _ => System.Windows.Controls.Dock.Bottom });
        slot.Children.Add(dot);
        slot.Children.Add(visual);
        slot.VerticalAlignment = Vertical ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        slot.HorizontalAlignment = Vertical ? (Edge == "Left" ? HorizontalAlignment.Left : HorizontalAlignment.Right) : HorizontalAlignment.Center;
        ToolTipService.SetPlacement(slot, Edge switch { "Left" => PlacementMode.Right, "Right" => PlacementMode.Left, _ => PlacementMode.Top });
        ToolTipService.SetInitialShowDelay(slot, 150);

        slot.PreviewMouseLeftButtonDown += (_, ev) => { _pressAt = ev.GetPosition(this); _pressed = e; };
        slot.MouseMove += (_, ev) =>
        {
            if (_pressed != e || ev.LeftButton != MouseButtonState.Pressed || e.Pin == null) return;
            var d = ev.GetPosition(this) - _pressAt;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _pressed = null;
            DragDrop.DoDragDrop(slot, new DataObject(PinFormat, S.Pinned.IndexOf(e.Pin)), DragDropEffects.Move);
        };
        slot.MouseLeftButtonUp += (_, _) => { if (_pressed == e) { _pressed = null; Click(e, visual); } };
        slot.MouseRightButtonUp += (_, ev) => { ev.Handled = true; ShowMenu(ItemMenu(e)); };

        Items.Children.Add(slot);
        _visuals.Add(visual); _baseSizes.Add(IconSize + Gap * 2); _scalable.Add(true);
        return slot;
    }

    void RefreshTrash()
    {
        if (_trashImage == null) return;
        bool full = Sys.TrashHasItems();
        if (full == _trashFull) return;
        _trashFull = full;
        _trashImage.Source = Icons.Override(full ? "trash-full" : "trash") ?? Icons.ShellUncached(RecycleBin);
    }

    // ---------- Clicks ----------
    void Click(Entry e, FrameworkElement visual)
    {
        if (e.Trash) { Sys.Launch("shell:RecycleBinFolder"); return; }
        var wins = WindowsFor(e);
        if (wins.Count == 0)
        {
            if (e.Pin == null) return;
            if (e.Pin.Launch != "::launchpad") Bounce(visual);
            Sys.Launch(e.Pin.Launch);
            return;
        }
        var fg = Native.GetForegroundWindow();
        if (wins.Any(w => w.Hwnd == fg))
        {
            // Already in front: cycle through its windows, or minimise a single one.
            if (wins.Count > 1) Native.Activate(wins[^1].Hwnd);
            else Native.SysCommand(fg, Native.SC_MINIMIZE);
        }
        else Native.Activate(wins[0].Hwnd);
    }

    void Bounce(FrameworkElement visual)
    {
        var t = (TranslateTransform)visual.RenderTransform;
        double h = IconSize * 0.4 * (Edge == "Left" ? 1 : -1);
        var anim = new DoubleAnimation(0, h, TimeSpan.FromMilliseconds(220))
        {
            AutoReverse = true, RepeatBehavior = new RepeatBehavior(2), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        t.BeginAnimation(Vertical ? TranslateTransform.XProperty : TranslateTransform.YProperty, anim);
    }

    // ---------- Menus ----------
    void ShowMenu(ContextMenu menu)
    {
        _menuOpen = true;
        menu.Closed += (_, _) => { _menuOpen = false; _lastNear = DateTime.Now; };
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    ContextMenu ItemMenu(Entry e)
    {
        if (e.Trash)
            return UI.Menu(UI.Item("Open", () => Sys.Launch("shell:RecycleBinFolder")),
                           UI.Item("Empty Recycle Bin", Sys.EmptyTrash, enabled: Sys.TrashHasItems()));

        var wins = WindowsFor(e);
        var items = new List<MenuItem?>();
        foreach (var w in wins)
        {
            var hwnd = w.Hwnd;
            items.Add(UI.Item(w.Title.Length > 60 ? w.Title[..60] + "…" : w.Title, () => Native.Activate(hwnd)));
        }
        if (wins.Count > 0) items.Add(null);

        string? launch = e.Pin?.Launch ?? wins.FirstOrDefault()?.Exe;
        if (e.Pin?.Launch != "::launchpad" && launch != null)
            items.Add(UI.Item(wins.Count > 0 ? "New Window" : "Open", () => Sys.Launch(launch)));
        if (e.Pin != null)
        {
            var pin = e.Pin;
            items.Add(UI.Item("Remove from Dock", () => { S.Pinned.Remove(pin); Settings.Save(); }));
        }
        else items.Add(UI.Item("Keep in Dock", () => { S.Pinned.Add(RunningApps.ToPin(wins[0])); Settings.Save(); }, enabled: wins.Count > 0));

        string? path = wins.FirstOrDefault()?.Exe ?? e.Pin?.Launch;
        if (path != null && (File.Exists(path) || Directory.Exists(path)))
            items.Add(UI.Item("Show in File Explorer", () => Sys.ShowInExplorer(path)));

        if (wins.Count > 0)
        {
            var key = wins[0].Key;
            items.Add(null);
            items.Add(UI.Item("Quit", () => MenuBar.QuitApp(key)));
        }
        return UI.Menu(items.ToArray());
    }

    void Shelf_RightClick(object sender, MouseButtonEventArgs e)
    {
        void Set(Action<Settings> change) { change(S); Settings.Save(); }
        ShowMenu(UI.Menu(
            UI.Item(S.DockAutoHide ? "Turn Hiding Off" : "Turn Hiding On", () => Set(s => s.DockAutoHide = !s.DockAutoHide)),
            UI.Item(S.DockMagnify ? "Turn Magnification Off" : "Turn Magnification On", () => Set(s => s.DockMagnify = !s.DockMagnify)),
            null,
            UI.Item("Position on Screen: Left", () => Set(s => s.DockPosition = "Left"), isChecked: Edge == "Left"),
            UI.Item("Position on Screen: Bottom", () => Set(s => s.DockPosition = "Bottom"), isChecked: Edge == "Bottom"),
            UI.Item("Position on Screen: Right", () => Set(s => s.DockPosition = "Right"), isChecked: Edge == "Right"),
            null,
            UI.Item("Dock Settings…", SettingsWindow.Open)));
    }

    // ---------- Magnification ----------
    void Magnify(Point? mouse)
    {
        double[] scales;
        if (mouse is { } p && S.DockMagnify)
        {
            double windowLen = Vertical ? Root.ActualHeight : Root.ActualWidth;
            double start = (windowLen - _baseSizes.Sum()) / 2; // the stack is centred on the edge
            scales = DockMath.Scales(_baseSizes, _scalable, (Vertical ? p.Y : p.X) - start, IconSize, S.DockMagnification);
        }
        else scales = Enumerable.Repeat(1.0, _visuals.Count).ToArray();

        for (int i = 0; i < _visuals.Count; i++)
            if (_visuals[i] is { } v) v.Width = v.Height = IconSize * scales[i];
    }

    // ---------- Auto-hide ----------
    void AutoHideTick()
    {
        if (!S.DockAutoHide || _fullscreenHidden) return;
        Native.GetCursorPos(out var pt);
        int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
        bool atEdge = Edge switch { "Left" => pt.X <= 1, "Right" => pt.X >= sw - 2, _ => pt.Y >= sh - 2 };
        if (atEdge || IsMouseOver || _menuOpen) _lastNear = DateTime.Now;
        if (!_autoShown && atEdge) SetAutoShown(true);
        else if (_autoShown && (DateTime.Now - _lastNear).TotalMilliseconds > 700) SetAutoShown(false);
    }

    void SetAutoShown(bool show)
    {
        _autoShown = show;
        double dist = Thickness + EdgeMargin + 10;
        var anim = new DoubleAnimation(show ? 0 : (Edge == "Left" ? -dist : dist), TimeSpan.FromMilliseconds(180));
        // The backdrop can't follow a WPF animation: drop it while sliding out, bring it back once in place.
        if (show) anim.Completed += (_, _) => SyncGlass(); else SyncGlass();
        Slide.BeginAnimation(Vertical ? TranslateTransform.XProperty : TranslateTransform.YProperty, anim);
    }

    // ---------- Drag & drop: reorder pins, or drop files/folders/shortcuts to pin them ----------
    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(PinFormat) ? DragDropEffects.Move
                  : e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        var pins = S.Pinned;
        var at = e.GetPosition(Items);
        int index = _pinSlots.TakeWhile(slot =>
        {
            var c = slot.TranslatePoint(new Point(slot.ActualWidth / 2, slot.ActualHeight / 2), Items);
            return Vertical ? at.Y > c.Y : at.X > c.X;
        }).Count();

        if (e.Data.GetData(PinFormat) is int from && from >= 0 && from < pins.Count)
        {
            var pin = pins[from];
            pins.RemoveAt(from);
            if (from < index) index--;
            pins.Insert(Math.Clamp(index, 0, pins.Count), pin);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var f in files) pins.Insert(Math.Min(index++, pins.Count), PinForFile(f));
        else return;
        Settings.Save();
    }

    static DockPin PinForFile(string path)
    {
        string? exe = path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                string target = sh.CreateShortcut(path).TargetPath;
                if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = target;
            }
            catch { /* shortcut to a non-file target; it still launches, just won't show a running dot */ }
        }
        string name = Directory.Exists(path) ? System.IO.Path.GetFileName(path) : System.IO.Path.GetFileNameWithoutExtension(path);
        return new DockPin { Name = name, Launch = path, Exe = exe };
    }
}
