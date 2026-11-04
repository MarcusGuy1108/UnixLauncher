using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;

namespace UnixLauncher.Views;

/// <summary>
/// Frosted-glass backgrounds. Windows 11 22H2+ draws real acrylic through DWM, which also supplies
/// the rounded corners, border and shadow. Older Windows can't round a blurred window, so panels
/// there stay solid (layered window, corners drawn by WPF).
/// </summary>
static class Glass
{
    public static readonly bool Supported = Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Call in the constructor, before the window is shown. Returns true if it gets acrylic.</summary>
    public static bool Setup(Window w, bool allow = true)
    {
        w.Background = Brushes.Transparent;
        if (!Supported || !allow)
        {
            w.WindowStyle = WindowStyle.None;
            w.AllowsTransparency = true;
            return false;
        }
        // A real (non-layered) window with its frame hidden: DWM only applies backdrops and rounding to those.
        w.WindowStyle = WindowStyle.SingleBorderWindow;
        w.AllowsTransparency = false;
        WindowChrome.SetWindowChrome(w, new WindowChrome
        {
            CaptionHeight = 0, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false,
        });
        w.SourceInitialized += (_, _) => Enable(UI.Hwnd(w));
        return true;
    }

    /// <summary>On a glass window, keep only a light tint on the panel; DWM draws corners, border and shadow.</summary>
    public static void Style(Border panel)
    {
        panel.Background = (Brush)Application.Current.Resources["GlassTint"];
        panel.BorderThickness = new Thickness(0);
        panel.CornerRadius = new CornerRadius(0);
    }

    static void Enable(IntPtr hwnd)
    {
        var src = HwndSource.FromHwnd(hwnd)!;
        src.CompositionTarget.BackgroundColor = Colors.Transparent;
        int dark = 1, round = 2, acrylic = 3;
        Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));    // DWMWA_USE_IMMERSIVE_DARK_MODE
        Native.DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));   // DWMWA_WINDOW_CORNER_PREFERENCE = round
        Native.DwmSetWindowAttribute(hwnd, 38, ref acrylic, sizeof(int)); // DWMWA_SYSTEMBACKDROP_TYPE = transient (acrylic)
        // Windows' "accent colour on title bars and borders" would paint an accent band over the glass and an accent rim:
        // no caption colour, and a fixed subtle grey rim like macOS's dark windows.
        int none = unchecked((int)0xFFFFFFFE), rim = 0x00484848; // DWMWA_COLOR_NONE; COLORREF 0x00BBGGRR
        Native.DwmSetWindowAttribute(hwnd, 35, ref none, sizeof(int));    // DWMWA_CAPTION_COLOR
        Native.DwmSetWindowAttribute(hwnd, 34, ref rim, sizeof(int));     // DWMWA_BORDER_COLOR
        src.AddHook(KeepActive);
        Native.SendMessage(hwnd, WM_NCACTIVATE, new IntPtr(1), IntPtr.Zero); // never-focused windows (dock, banners) start "inactive"
    }

    const int WM_NCACTIVATE = 0x0086;

    // DWM swaps acrylic for a flat colour on inactive windows; the dock and banners are never active,
    // so always report the frame as active. lParam -1 = don't repaint the (hidden) non-client area.
    static IntPtr KeepActive(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg != WM_NCACTIVATE) return IntPtr.Zero;
        handled = true;
        return Native.DefWindowProc(h, msg, new IntPtr(1), new IntPtr(-1));
    }
}

static class UI
{
    public static IntPtr Hwnd(Window w) => new WindowInteropHelper(w).EnsureHandle();


    /// <summary>Marks an element as one floating glass shape (see <see cref="FloatingGlass"/>), with this corner radius.</summary>
    public static readonly DependencyProperty GlassRadiusProperty =
        DependencyProperty.RegisterAttached("GlassRadius", typeof(double), typeof(UI), new PropertyMetadata(double.NaN));
    public static double GetGlassRadius(DependencyObject d) => (double)d.GetValue(GlassRadiusProperty);
    public static void SetGlassRadius(DependencyObject d, double v) => d.SetValue(GlassRadiusProperty, v);

    /// <summary>
    /// macOS 27 panels made of separate pieces of glass (Control Centre's tiles, Spotlight's field and results): behind the
    /// content sits a blurred copy of the screen (<see cref="Backdrop"/>), clipped to the elements marked with UI.GlassRadius,
    /// so each piece has exact rounded corners and the gaps between them stay clear and click through.
    /// </summary>
    public static void FloatingGlass(Window w)
    {
        var content = (UIElement)w.Content;
        var picture = new Image { Stretch = Stretch.Fill };
        var layer = new Canvas { IsHitTestVisible = false, Children = { picture } };
        w.Content = null;
        w.Content = new Grid { Children = { layer, content } };

        w.IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) return;
            // The whole screen under the panel: it can grow after opening (Spotlight's results).
            IntPtr h = Hwnd(w);
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(h, 2), ref mi) || !Native.GetWindowRect(h, out var wr)) return;
            var mon = mi.rcMonitor;
            double s = Scale(w);
            picture.Source = Backdrop.Capture(mon, Color.FromRgb(0x1E, 0x1E, 0x20), 0.15);
            picture.Width = (mon.Right - mon.Left) / s;
            picture.Height = (mon.Bottom - mon.Top) / s;
            Canvas.SetLeft(picture, (mon.Left - wr.Left) / s);
            Canvas.SetTop(picture, (mon.Top - wr.Top) / s);
        };

        string last = "";
        w.LayoutUpdated += (_, _) =>
        {
            if (w.Content is not Grid root) return;
            var shapes = new List<(Rect r, double rad)>();
            CollectShapes(root, content, new Rect(-1e6, -1e6, 2e6, 2e6), shapes);
            string sig = string.Join(";", shapes);
            if (sig == last) return; // layout passes run all the time: only re-clip when a shape moved
            last = sig;
            var clip = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var (r, rad) in shapes) clip.Children.Add(new RectangleGeometry(r, rad, rad));
            layer.Clip = clip;
        };
    }

    static void CollectShapes(Visual root, DependencyObject d, Rect clip, List<(Rect, double)> shapes)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            if (child is ScrollViewer sv && sv.ActualHeight > 0) // shapes scrolled out of view get no glass
            {
                var view = sv.TransformToAncestor(root).TransformBounds(new Rect(sv.RenderSize));
                view.Intersect(clip);
                if (!view.IsEmpty) CollectShapes(root, child, view, shapes);
                continue;
            }
            if (child is FrameworkElement fe && !double.IsNaN(GetGlassRadius(fe)))
            {
                if (fe.Visibility != Visibility.Visible || fe.ActualWidth == 0) continue;
                var b = fe.TransformToAncestor(root).TransformBounds(new Rect(fe.RenderSize));
                double rad = Math.Min(GetGlassRadius(fe), Math.Min(b.Width, b.Height) / 2);
                b.Intersect(clip);
                if (!b.IsEmpty && b.Height >= 1) shapes.Add((b, rad));
                continue; // a shape's insides don't add shapes
            }
            CollectShapes(root, child, clip, shapes);
        }
    }

    /// <summary>Device pixels per DIP for this window (DPI scale).</summary>
    public static double Scale(Visual v) => PresentationSource.FromVisual(v)?.CompositionTarget?.TransformToDevice.M11 ?? 1;

    /// <summary>A user-supplied icon (icons/&lt;name&gt;.png) if present, else a Segoe Fluent/MDL2 glyph.</summary>
    /// <param name="inheritColor">Leave the glyph colour to the container (e.g. a Control Centre button that turns it blue when on).</param>
    public static FrameworkElement Icon(string overrideName, string glyph, double size, bool inheritColor = false)
    {
        if (Icons.Override(overrideName) is { } img) return new Image { Source = img, Width = size, Height = size };
        var tb = new TextBlock
        {
            Text = glyph, FontSize = size, FontFamily = (FontFamily)Application.Current.Resources["GlyphFont"],
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        if (!inheritColor) tb.Foreground = (Brush)Application.Current.Resources["Text"];
        return tb;
    }

    static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    static readonly IEasingFunction EaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };

    /// <summary>
    /// Windows' "Animation effects" switch (Settings › Accessibility › Visual effects). Off = no launcher animations either,
    /// the same as macOS "Reduce motion".
    /// </summary>
    public static bool Motion => SystemParameters.ClientAreaAnimation;

    /// <summary>macOS-style appearance: a quick fade in while growing from slightly smaller.</summary>
    public static void PopIn(FrameworkElement el, double from = 0.96, Point? origin = null, int ms = 180)
    {
        if (!Motion)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.RenderTransform = null;
            return;
        }
        var dur = TimeSpan.FromMilliseconds(ms);
        var scale = new ScaleTransform(from, from);
        el.RenderTransformOrigin = origin ?? new Point(0.5, 0.5);
        el.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(from, 1, dur) { EasingFunction = EaseOut });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(from, 1, dur) { EasingFunction = EaseOut });
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur));
    }

    /// <summary>The reverse of PopIn: fade while shrinking, then <paramref name="done"/>. The element is left invisible.</summary>
    public static void PopOut(FrameworkElement el, Action done, double to = 0.97, Point? origin = null, int ms = 140)
    {
        if (!Motion) { done(); return; }
        var dur = TimeSpan.FromMilliseconds(ms);
        var scale = new ScaleTransform(1, 1);
        el.RenderTransformOrigin = origin ?? new Point(0.5, 0.5);
        el.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, to, dur) { EasingFunction = EaseIn });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, to, dur) { EasingFunction = EaseIn });
        var fade = new DoubleAnimation(0, dur);
        fade.Completed += (_, _) => done();
        el.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>macOS menu glass: the blurred screen behind the menu, inside its rounded panel (see <see cref="Backdrop"/>).</summary>
    public static void GlassMenu(ContextMenu menu)
    {
        if (VisualTreeHelper.GetChildrenCount(menu) > 0 && VisualTreeHelper.GetChild(menu, 0) is Border panel) GlassPanel(panel);
    }

    /// <summary>
    /// Close an open menu when a mouse button goes down anywhere outside the launcher. Our dock and bar never take the
    /// foreground (the app keeps it, as on macOS), and without it a WPF menu can't hold the mouse, so outside clicks were missed.
    /// shortcut: polled every 40 ms while a menu is open; Escape still goes to the app in front.
    /// </summary>
    public static void CloseOnOutsideClick(ContextMenu menu)
    {
        // Held now, or pressed since the last look (a quick click can fall between two ticks): left, right, middle.
        static bool Pressed()
        {
            short l = Native.GetAsyncKeyState(0x01), r = Native.GetAsyncKeyState(0x02), m = Native.GetAsyncKeyState(0x04); // read all: each clears its bit
            return (l & 0x8001) != 0 || (r & 0x8001) != 0 || (m & 0x8001) != 0;
        }
        Pressed(); // forget the click that opened the menu
        var poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        poll.Tick += (_, _) =>
        {
            if (!menu.IsOpen) { poll.Stop(); return; }
            if (Pressed() && Native.GetCursorPos(out var p) && !RunningApps.IsOwn(Native.WindowFromPoint(p))) menu.IsOpen = false;
        };
        poll.Start();
    }

    /// <summary>The same glass for a submenu's panel (MenuItem's PART_Popup).</summary>
    public static void GlassPanel(Border panel)
    {
        if (panel.ActualWidth == 0) return;
        if (Backdrop.Brush(Backdrop.ScreenRect(panel), Color.FromRgb(0x24, 0x24, 0x26), 0.5) is { } glass) panel.Background = glass;
    }

    /// <summary>Activate a launcher window; falls back to forcing it forward when Windows' focus rules refuse (hook hotkeys).</summary>
    public static void BringToFront(Window w)
    {
        if (!w.Activate()) Native.ForceForeground(Hwnd(w));
    }

    /// <summary>A shortcut that only exists with Alt as ⌘ (Mac shortcuts on), e.g. ⌘H: shown then, otherwise blank.</summary>
    public static string MacOnly(string keys) => Settings.Current.MacShortcuts ? keys : "";

    public static MenuItem Item(string header, Action action, bool enabled = true, bool isChecked = false, string keys = "")
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled, IsChecked = isChecked, InputGestureText = Settings.Current.MacShortcuts ? MenuText.MacKeys(keys) : keys };
        mi.Click += (_, _) => action();
        return mi;
    }

    /// <summary>Build a context menu; null entries become separators (never leading, trailing or doubled).</summary>
    /// <summary>A submenu item (macOS "Options ›"); null entries become separators, as in <see cref="Menu"/>.</summary>
    public static MenuItem Sub(string header, params MenuItem?[] items)
    {
        var mi = new MenuItem { Header = header };
        foreach (var i in items)
        {
            if (i != null) mi.Items.Add(i);
            else if (mi.Items.Count > 0 && mi.Items[^1] is not Separator) mi.Items.Add(new Separator());
        }
        if (mi.Items.Count > 0 && mi.Items[^1] is Separator) mi.Items.RemoveAt(mi.Items.Count - 1);
        return mi;
    }

    public static ContextMenu Menu(params MenuItem?[] items)
    {
        var cm = new ContextMenu();
        foreach (var i in items)
        {
            if (i != null) cm.Items.Add(i);
            else if (cm.Items.Count > 0 && cm.Items[^1] is not Separator) cm.Items.Add(new Separator());
        }
        if (cm.Items.Count > 0 && cm.Items[^1] is Separator) cm.Items.RemoveAt(cm.Items.Count - 1);
        return cm;
    }

    public static void ShowMenu(ContextMenu menu, UIElement under)
    {
        menu.PlacementTarget = under;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}

/// <summary>Panel that opens on demand and hides when it loses focus (Control Centre, Spotlight…).</summary>
public class Popover : Window
{
    /// <summary>Full-screen Launchpad opts out: it uses the rectangular blur instead.</summary>
    protected virtual bool UseGlass => true;

    public Popover()
    {
        bool glass = Glass.Setup(this, UseGlass);
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(FontFamilyProperty, "UiFont");
        Deactivated += (_, _) => Dismiss();
        SourceInitialized += (_, _) =>
        {
            Native.SetToolWindow(UI.Hwnd(this), false);
            if (glass && Content is Border panel) Glass.Style(panel);
        };
    }

    /// <summary>Where the pop-in grows from: the top edge for panels that drop down from the menu bar.</summary>
    protected virtual Point AnimationOrigin => new(0.5, 0);
    protected virtual void AnimateIn(FrameworkElement root) => UI.PopIn(root, origin: AnimationOrigin);
    protected virtual void AnimateOut(FrameworkElement root, Action done) => UI.PopOut(root, done, origin: AnimationOrigin);

    int _shown; // bumped on every Open so a fade-out that's still running can't hide the reopened panel

    public void Toggle() { if (IsVisible) Dismiss(); else Open(); }

    public void Open()
    {
        _shown++;
        OnOpening();
        Show();
        UI.BringToFront(this);
        if (Content is FrameworkElement root) AnimateIn(root);
    }

    /// <summary>Close the way macOS panels do: a short fade, then hide.</summary>
    public void Dismiss()
    {
        if (!IsVisible) return;
        if (Content is not FrameworkElement root) { Hide(); return; }
        int shown = _shown;
        AnimateOut(root, () =>
        {
            if (shown != _shown) return;
            Hide();
            root.BeginAnimation(OpacityProperty, null);
        });
    }

    protected virtual void OnOpening() { }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Dismiss();
        base.OnKeyDown(e);
    }
}

/// <summary>A launcher window with macOS chrome: rounded, red/yellow/green buttons, drag anywhere on the title bar.</summary>
public class MacWindow : Window
{
    Rect? _restore;
    readonly bool _glass;
    bool _closingNow, _minimised;

    /// <summary>Close with a short fade, like macOS. Turn off for dialogs (ShowDialog), so their result isn't disturbed.</summary>
    public bool AnimateClose { get; set; } = true;

    /// <summary>Finder-style: no title bar row; content fills the window and draws its own toolbar beside the lights.</summary>
    protected bool UnifiedToolbar { get; set; }
    protected const double ToolbarHeight = 52;

    public MacWindow()
    {
        _glass = Glass.Setup(this);
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(StyleProperty, "MacWindow");
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Root") is FrameworkElement opening) Loaded += (_, _) => UI.PopIn(opening, 0.94, ms: 220); // windows zoom open
        if (_glass && GetTemplateChild("PART_Root") is FrameworkElement root && GetTemplateChild("PART_Bg") is Border bg)
        {
            root.Margin = new Thickness(0); // the margin only made room for WPF's own drop shadow
            bg.Effect = null;
            Glass.Style(bg);
        }
        if (UnifiedToolbar && GetTemplateChild("PART_Content") is FrameworkElement content && GetTemplateChild("PART_TitleBar") is Panel titleBar)
        {
            content.Margin = new Thickness(0);
            titleBar.Background = null;     // only the lights stay clickable; the window's toolbar handles dragging
            titleBar.Height = ToolbarHeight;
            if (GetTemplateChild("PART_Title") is UIElement title) title.Visibility = Visibility.Collapsed;
            // Layered windows don't clip to the rounded corners (on glass, DWM does).
            if (!_glass) content.SizeChanged += (_, e) => content.Clip = new RectangleGeometry(new Rect(e.NewSize), 10, 10);
        }
        if (GetTemplateChild("PART_Close") is Button close) close.Click += (_, _) => Close();
        if (GetTemplateChild("PART_Min") is Button min) min.Click += (_, _) => Minimise();
        if (GetTemplateChild("PART_Zoom") is Button zoom) zoom.Click += (_, _) => Zoom();
        if (GetTemplateChild("PART_TitleBar") is FrameworkElement bar)
            bar.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Zoom(); else DragMove(); };
    }

    FrameworkElement? Root => GetTemplateChild("PART_Root") as FrameworkElement;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _closingNow || !AnimateClose || !UI.Motion || Root is not { } root) return;
        e.Cancel = true; // close after the fade
        _closingNow = true;
        UI.PopOut(root, () => Dispatcher.BeginInvoke(() => { if (IsVisible) Close(); }), 0.96);
    }

    /// <summary>macOS "Scale" minimise: shrink down towards the dock, then minimise.</summary>
    public void Minimise()
    {
        if (Root is not { } root) { WindowState = WindowState.Minimized; return; }
        UI.PopOut(root, () =>
        {
            WindowState = WindowState.Minimized;
            root.BeginAnimation(OpacityProperty, null);
        }, 0.6, new Point(0.5, 1), 200);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized) _minimised = true;
        else if (_minimised && Root is { } root)
        {
            _minimised = false;
            UI.PopIn(root, 0.6, new Point(0.5, 1), 220); // grow back up out of the dock
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (ResizeMode is not (ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)) return;
        // Borderless windows have no resize frame of their own. On layered windows the edge sits 16px in (shadow margin).
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome == null)
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0, GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false, ResizeBorderThickness = new Thickness(20),
            });
        else chrome.ResizeBorderThickness = new Thickness(6);
    }

    /// <summary>macOS "zoom": fill the work area (borderless WPF maximise would cover the menu bar and dock).</summary>
    public void Zoom()
    {
        if (_restore is { } r) { Left = r.Left; Top = r.Top; Width = r.Width; Height = r.Height; _restore = null; return; }
        _restore = new Rect(Left, Top, Width, Height);
        var wa = SystemParameters.WorkArea;
        Left = wa.Left; Top = wa.Top; Width = wa.Width; Height = wa.Height;
    }
}
