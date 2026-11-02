using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Runtime.InteropServices;
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
    readonly List<(Entry entry, FrameworkElement slot)> _entrySlots = new();
    int _shellHookMsg;
    Image? _trashImage;
    bool? _trashFull;

    bool _fullscreenHidden, _autoShown = true, _menuOpen, _dragging;
    DockPin? _dragNew; // a running app being dragged into the pinned section
    FrameworkElement? _dragSlot, _gap; // the icon being dragged; the space opening where it will land
    int _gapAt = -1;
    DateTime _lastNear = DateTime.Now;
    Point _pressAt;
    Entry? _pressed;

    static Settings S => Settings.Current;
    static string Edge => S.DockPosition;
    static bool Vertical => Edge is "Left" or "Right";
    static double IconSize => S.DockIconSize;
    static double Thickness => IconSize + Gap * 2 + Pad * 2 + DotRoom;
    static double ShelfRadius => Math.Round(Thickness * 0.3); // macOS 26's generously rounded shelf

    public DockWindow()
    {
        InitializeComponent();
        {
            // This window spans the whole screen edge (room for magnified icons), so blurring it would blur
            // that entire strip. Instead a separate shelf-sized blur window sits directly underneath. It uses the
            // menu bar's accent blur: the system acrylic ignores the rounded window region and showed square corners.
            _glass = new Window
            {
                ShowInTaskbar = false, ShowActivated = false, Topmost = true, ResizeMode = ResizeMode.NoResize, Title = "Dock Backdrop",
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            };
            _glass.SourceInitialized += (_, _) => { Native.SetToolWindow(UI.Hwnd(_glass), true); Native.EnableBlur(UI.Hwnd(_glass)); };
            ShelfBg.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)); // light glass over the real blur
            ShelfBg.SizeChanged += (_, _) => SyncGlass();
        }
        SourceInitialized += (_, _) =>
        {
            _hwnd = UI.Hwnd(this);
            Native.SetToolWindow(_hwnd, true);
            if (App.ShellMode) // with no Explorer taskbar, we're who Windows asks where minimised windows go: into their dock icon
            {
                _shellHookMsg = (int)Native.RegisterWindowMessage("SHELLHOOK");
                HwndSource.FromHwnd(_hwnd)!.AddHook(ShellHook);
                Native.RegisterShellHookWindow(_hwnd);
                Native.SetTaskmanWindow(_hwnd);
            }
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
        DragLeave += OnDragLeave;
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
        ShelfBg.CornerRadius = new CornerRadius(ShelfRadius);
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
        int w = (int)Math.Round(b.X - a.X), h = (int)Math.Round(b.Y - a.Y), r = (int)Math.Round(ShelfRadius * 2 * UI.Scale(this));
        _glass.Show();
        var glass = UI.Hwnd(_glass);
        Native.SetWindowPos(glass, _hwnd, (int)a.X, (int)a.Y, w, h, Native.SWP_NOACTIVATE);
        // DWM only rounds windows by 8px; clip the blur to the shelf's own radius (the shelf's rim hides the region's hard edge).
        Native.SetWindowRgn(glass, Native.CreateRoundRectRgn(0, 0, w + 1, h + 1, r, r), true); // Windows owns the region afterwards
    }

    // ---------- Items ----------
    void RefreshWindows()
    {
        if (_dragging) return; // rebuilding the icons would pull the dragged one out from under the pointer
        _windows = RunningApps.Enumerate();
        var pins = S.Pinned;
        var entries = pins.Select(p => new Entry { Pin = p, Windows = PinWindows(p) }).ToList();
        entries.AddRange(_windows.Where(w => !pins.Any(p => Matches(p, w)))
                                 .GroupBy(w => w.Key)
                                 .Select(g => new Entry { Key = g.Key, Windows = g.ToList() }));

        string sig = string.Join("|", entries.Select(e => (e.Pin?.Launch ?? e.Key) + (e.Windows.Count > 0 ? "*" : "")));
        if (sig == _signature) return;
        _signature = sig;
        Rebuild(entries, pins.Count);
    }

    static bool Matches(DockPin p, WinInfo w) => AppMatch.Matches(p.Exe, p.Aumid, w.Exe, w.Aumid);

    List<WinInfo> WindowsFor(Entry e) => e.Pin != null ? PinWindows(e.Pin) : _windows.Where(w => w.Key == e.Key).ToList();

    // Finder windows belong to this process, so they never appear in the running-apps scan.
    List<WinInfo> PinWindows(DockPin p) => p.Launch == "::finder"
        ? FinderWindow.All.Select(f => new WinInfo { Hwnd = UI.Hwnd(f), Title = f.Title }).ToList()
        : _windows.Where(w => Matches(p, w)).ToList();

    void Rebuild(List<Entry> entries, int pinCount)
    {
        Items.Children.Clear();
        _visuals.Clear(); _baseSizes.Clear(); _scalable.Clear(); _pinSlots.Clear(); _entrySlots.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            if (i == pinCount) AddSeparator(); // pinned | running-but-not-pinned
            var slot = AddIcon(entries[i]);
            _entrySlots.Add((entries[i], slot));
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
        else if (e.Pin is { Launch: "::finder" })
        {
            name = e.Pin.Name; // no Finder artwork is shipped: icons/finder.png, else Explorer's icon
            icon = Icons.Override("finder") ?? Icons.Shell(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        }
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
            if (_pressed != e || ev.LeftButton != MouseButtonState.Pressed || e.Trash) return;
            var d = ev.GetPosition(this) - _pressAt;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _pressed = null;
            DragItem(e, slot);
        };
        slot.MouseLeftButtonUp += (_, _) => { if (_pressed == e) { _pressed = null; Click(e, visual); } };
        slot.MouseRightButtonUp += (_, ev) => { ev.Handled = true; ShowMenu(ItemMenu(e), slot); };

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
        if (e.Trash) { FinderWindow.Open(FinderWindow.TrashPath); return; }
        if (e.Pin != null && Directory.Exists(e.Pin.Launch)) { StackPopover.Toggle(e.Pin.Launch, visual, Edge); return; }
        var wins = WindowsFor(e);
        if (wins.Count == 0)
        {
            if (e.Pin == null) return;
            if (e.Pin.Launch != "::launchpad") Bounce(visual);
            Sys.Launch(e.Pin.Launch);
            return;
        }
        // macOS: bring the app forward (never minimise it); minimised windows return only if nothing else is open.
        Native.Activate((wins.FirstOrDefault(w => !Native.IsIconic(w.Hwnd)) ?? wins[0]).Hwnd);
    }

    /// <summary>
    /// macOS launch bounce: keeps going until the app's first window appears. That changes the dock's running state, which
    /// rebuilds the icons and so ends the bounce; apps that never show a window stop after 10 seconds.
    /// </summary>
    void Bounce(FrameworkElement visual)
    {
        if (!UI.Motion) return;
        var t = (TranslateTransform)visual.RenderTransform;
        var axis = Vertical ? TranslateTransform.XProperty : TranslateTransform.YProperty;
        double h = IconSize * 0.4 * (Edge == "Left" ? 1 : -1);
        t.BeginAnimation(axis, new DoubleAnimation(0, h, TimeSpan.FromMilliseconds(300))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
        var giveUp = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        giveUp.Tick += (_, _) => { giveUp.Stop(); t.BeginAnimation(axis, null); };
        giveUp.Start();
    }

    // ---------- Menus ----------
    /// <summary>Icon menus open centred just off the icon, towards the screen, as on macOS; shelf menus at the pointer.</summary>
    void ShowMenu(ContextMenu menu, FrameworkElement? icon = null)
    {
        _menuOpen = true;
        menu.Closed += (_, _) => { _menuOpen = false; _lastNear = DateTime.Now; };
        if (icon == null) menu.Placement = PlacementMode.MousePoint;
        else
        {
            menu.PlacementTarget = icon;
            menu.Placement = PlacementMode.Custom;
            double gap = 10 * UI.Scale(this); // popup sizes here are in device pixels
            menu.CustomPopupPlacementCallback = (popup, target, _) => new[]
            {
                new CustomPopupPlacement(Edge switch
                {
                    "Left" => new Point(target.Width + gap, (target.Height - popup.Height) / 2),
                    "Right" => new Point(-popup.Width - gap, (target.Height - popup.Height) / 2),
                    _ => new Point((target.Width - popup.Width) / 2, -popup.Height - gap),
                }, PopupPrimaryAxis.None),
            };
        }
        menu.IsOpen = true;
    }

    ContextMenu ItemMenu(Entry e)
    {
        if (e.Trash)
            return UI.Menu(UI.Item("Open", () => FinderWindow.Open(FinderWindow.TrashPath)),
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
            items.Add(UI.Item("Show in Finder", () => Sys.ShowInFinder(path)));

        if (wins.Count > 0 && e.Pin?.Launch == "::finder")
        {
            items.Add(null);
            items.Add(UI.Item("Hide", () => { foreach (var f in FinderWindow.All) f.Minimise(); }));
            items.Add(UI.Item("Quit", () => { foreach (var f in FinderWindow.All.ToList()) f.Close(); }));
        }
        else if (wins.Count > 0)
        {
            var (key, hwnd, title) = (wins[0].Key, wins[0].Hwnd, RunningApps.DisplayName(wins[0]));
            items.Add(null);
            items.Add(UI.Item("Hide", () => MenuBar.HideApp(key)));
            items.Add(UI.Item("Quit", () => MenuBar.QuitApp(key)));
            items.Add(UI.Item("Force Quit", () => MenuBar.ForceQuit(hwnd, title))); // macOS shows this with ⌥ held
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
            UI.Item("Dock Settings…", () => SettingsWindow.Open("Desktop & Dock"))));
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
        {
            if (_visuals[i] is not { } v) continue;
            double size = IconSize * scales[i];
            if (mouse == null && UI.Motion && Math.Abs(v.ActualWidth - size) > 0.5)
            {
                // The pointer left: settle back smoothly, as the macOS dock does.
                var settle = new DoubleAnimation(v.ActualWidth, size, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop,
                };
                v.Width = v.Height = size;
                v.BeginAnimation(WidthProperty, settle);
                v.BeginAnimation(HeightProperty, settle);
                continue;
            }
            v.BeginAnimation(WidthProperty, null); // the pointer is back: follow it directly
            v.BeginAnimation(HeightProperty, null);
            v.Width = v.Height = size;
        }
    }

    // ---------- Auto-hide ----------
    void AutoHideTick()
    {
        if (!S.DockAutoHide || _fullscreenHidden) return;
        Native.GetCursorPos(out var pt);
        int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
        bool atEdge = Edge switch { "Left" => pt.X <= 1, "Right" => pt.X >= sw - 2, _ => pt.Y >= sh - 2 };
        if (atEdge || IsMouseOver || _menuOpen || _dragging) _lastNear = DateTime.Now;
        if (!_autoShown && atEdge) SetAutoShown(true);
        else if (_autoShown && (DateTime.Now - _lastNear).TotalMilliseconds > 700) SetAutoShown(false);
    }

    void SetAutoShown(bool show)
    {
        _autoShown = show;
        double dist = Thickness + EdgeMargin + 10;
        var anim = new DoubleAnimation(show ? 0 : (Edge == "Left" ? -dist : dist), TimeSpan.FromMilliseconds(UI.Motion ? 180 : 0));
        // The backdrop can't follow a WPF animation: drop it while sliding out, bring it back once in place.
        if (show) anim.Completed += (_, _) => SyncGlass(); else SyncGlass();
        Slide.BeginAnimation(Vertical ? TranslateTransform.XProperty : TranslateTransform.YProperty, anim);
    }

    // ---------- Minimise target (shell mode) ----------
    IntPtr ShellHook(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg != _shellHookMsg || (wp.ToInt64() & 0x7FFF) != 5 || lp == IntPtr.Zero) return IntPtr.Zero; // HSHELL_GETMINRECT
        var info = Marshal.PtrToStructure<Native.SHELLHOOKINFO>(lp);
        var w = RunningApps.Describe(info.hwnd);
        foreach (var (entry, slot) in _entrySlots)
        {
            if (!(entry.Pin != null ? Matches(entry.Pin, w) : entry.Key == w.Key) || !slot.IsVisible) continue;
            var a = slot.PointToScreen(new Point(0, 0));
            var b = slot.PointToScreen(new Point(slot.ActualWidth, slot.ActualHeight));
            info.rc = new Native.RECT { Left = (int)a.X, Top = (int)a.Y, Right = (int)b.X, Bottom = (int)b.Y };
            Marshal.StructureToPtr(info, lp, false);
            handled = true;
            return new IntPtr(1);
        }
        return IntPtr.Zero;
    }

    // ---------- Drag & drop: reorder pins, pin running apps, drag pins off to remove, or drop files/folders/shortcuts to pin them ----------
    void DragItem(Entry e, FrameworkElement slot)
    {
        var wins = WindowsFor(e);
        if (e.Pin == null && wins.Count == 0) return;
        _dragNew = e.Pin == null ? RunningApps.ToPin(wins[0]) : null;
        bool removable = e.Pin is { Launch: not "::finder" }; // Finder always stays, as on macOS
        var (ghost, label) = Ghost(slot);
        bool cancelled = false;
        QueryContinueDragEventHandler esc = (_, q) => cancelled |= q.EscapePressed;
        GiveFeedbackEventHandler follow = (_, g) =>
        {
            Native.GetCursorPos(out var c);
            double sc = UI.Scale(this);
            ghost.Left = c.X / sc - ghost.ActualWidth / 2;
            ghost.Top = c.Y / sc - IconSize / 2;
            label.Visibility = removable && PointerOffDock() ? Visibility.Visible : Visibility.Hidden;
            g.UseDefaultCursors = false; // macOS keeps the plain arrow; the icon itself is the feedback
            Mouse.SetCursor(Cursors.Arrow);
            g.Handled = true;
        };
        slot.QueryContinueDrag += esc;
        slot.GiveFeedback += follow;
        _dragging = true;
        _dragSlot = slot;
        Magnify(null);
        Resize(slot, Vertical ? slot.ActualHeight + Gap * 2 : slot.ActualWidth + Gap * 2, 0, null); // its space closes as the gap opens
        slot.Margin = new Thickness(0);
        slot.ClipToBounds = true;
        slot.Opacity = 0;
        DragDropEffects result;
        try { result = DragDrop.DoDragDrop(slot, new DataObject(PinFormat, e.Pin == null ? -1 : S.Pinned.IndexOf(e.Pin)), DragDropEffects.Move); }
        finally { _dragging = false; _dragNew = null; _dragSlot = null; slot.QueryContinueDrag -= esc; slot.GiveFeedback -= follow; }

        if (result == DragDropEffects.None && !cancelled && removable && PointerOffDock())
        {
            Puff(ghost);
            S.Pinned.Remove(e.Pin!);
            Settings.Save();
            return;
        }
        ghost.Close();
        _signature = ""; // put the dragged icon back (or show the new order)
        RefreshWindows();
    }

    /// <summary>The icon under the pointer while dragging, with the "Remove" label macOS shows off the Dock. Click-through, so drops land beneath it.</summary>
    (Window, FrameworkElement) Ghost(FrameworkElement slot)
    {
        var visual = slot is Panel { Children.Count: > 1 } p ? (FrameworkElement)p.Children[1] : slot;
        double s = UI.Scale(this), size = IconSize;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(size * s), (int)Math.Ceiling(size * s), 96 * s, 96 * s, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, size, size));
        bmp.Render(dv);
        var label = new Border
        {
            Background = (Brush)FindResource("PanelBg"), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 8, 3),
            Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Hidden,
            Child = new TextBlock { Text = "Remove", Foreground = (Brush)FindResource("Text"), FontSize = 12 },
        };
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
            ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight,
            FontFamily = FontFamily, Left = -10000, Top = -10000,
            Content = new StackPanel { MinWidth = 80, Children = { new Image { Source = bmp, Width = size, Height = size }, label } },
        };
        w.SourceInitialized += (_, _) =>
        {
            var h = UI.Hwnd(w);
            Native.SetToolWindow(h, true);
            Native.SetWindowLongPtr(h, -20, new IntPtr(Native.ExStyle(h) | 0x20)); // GWL_EXSTYLE |= WS_EX_TRANSPARENT
        };
        w.Show();
        return (w, label);
    }

    /// <summary>macOS's little cloud as an icon leaves the Dock: it swells and vanishes.</summary>
    static void Puff(Window ghost)
    {
        if (!UI.Motion || ghost.Content is not FrameworkElement c) { ghost.Close(); return; }
        var grow = new ScaleTransform(1, 1);
        c.RenderTransformOrigin = new Point(0.5, 0.3);
        c.RenderTransform = grow;
        var dur = TimeSpan.FromMilliseconds(220);
        grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.4, dur));
        grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.4, dur));
        var fade = new DoubleAnimation(0, dur);
        fade.Completed += (_, _) => ghost.Close();
        c.BeginAnimation(OpacityProperty, fade);
    }

    // Pinned icons other than the one being dragged, in order: drop positions count only these.
    List<FrameworkElement> OtherPins => _pinSlots.Where(x => x != _dragSlot).ToList();

    int DropIndex(DragEventArgs e)
    {
        var at = e.GetPosition(Items);
        return OtherPins.TakeWhile(slot =>
        {
            var c = slot.TranslatePoint(new Point(slot.ActualWidth / 2, slot.ActualHeight / 2), Items);
            return Vertical ? at.Y > c.Y : at.X > c.X;
        }).Count();
    }

    double SlotLen => IconSize + Gap * 2;

    /// <summary>Open an icon-sized space where the drop would land; the old space closes as the new one opens, so icons slide.</summary>
    void ShowGap(int index)
    {
        if (index == _gapAt && _gap != null) return;
        CloseGap();
        _gapAt = index;
        var others = OtherPins;
        int at = index < others.Count ? Items.Children.IndexOf(others[index])
               : _pinSlots.Count > 0 ? Items.Children.IndexOf(_pinSlots[^1]) + 1 : 0;
        var gap = new Border { Background = Brushes.Transparent }; // non-null: the gap still accepts the drop
        if (Vertical) gap.Width = 1; else gap.Height = 1;
        Items.Children.Insert(Math.Max(0, at), gap);
        _gap = gap;
        Resize(gap, 0, SlotLen, null);
    }

    void CloseGap()
    {
        _gapAt = -1;
        if (_gap is not { } g) return;
        _gap = null;
        Resize(g, Vertical ? g.ActualHeight : g.ActualWidth, 0, () => Items.Children.Remove(g));
    }

    void Resize(FrameworkElement el, double from, double to, Action? done)
    {
        var prop = Vertical ? HeightProperty : WidthProperty;
        if (!UI.Motion) { el.BeginAnimation(prop, null); el.SetValue(prop, to); done?.Invoke(); return; }
        var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (done != null) a.Completed += (_, _) => done();
        el.BeginAnimation(prop, a);
    }

    bool PointerOffDock()
    {
        Native.GetCursorPos(out var p);
        Native.GetWindowRect(_hwnd, out var r);
        return p.X < r.Left || p.X >= r.Right || p.Y < r.Top || p.Y >= r.Bottom;
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(PinFormat) ? DragDropEffects.Move
                  : e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
        _lastNear = DateTime.Now; // an auto-hiding Dock stays up while something is dragged over it
        if (e.Effects != DragDropEffects.None) ShowGap(DropIndex(e));
    }

    void OnDragLeave(object sender, DragEventArgs e)
    {
        // Leave also fires moving between icons; only close the gap once the pointer is really off the shelf.
        var p = e.GetPosition(Shelf);
        if (p.X >= 0 && p.Y >= 0 && p.X < Shelf.ActualWidth && p.Y < Shelf.ActualHeight) return;
        CloseGap();
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        var pins = S.Pinned;
        int index = DropIndex(e); // counted without the dragged icon, so it is already the final position
        CloseGap();

        if (e.Data.GetData(PinFormat) is int from)
        {
            if (from >= 0 && from < pins.Count)
            {
                var pin = pins[from];
                pins.RemoveAt(from);
                pins.Insert(Math.Clamp(index, 0, pins.Count), pin);
            }
            else if (_dragNew != null) pins.Insert(Math.Clamp(index, 0, pins.Count), _dragNew); // Keep in Dock by dragging
            else return;
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
