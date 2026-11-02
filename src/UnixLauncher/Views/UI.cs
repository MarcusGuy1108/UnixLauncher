using System;
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

    /// <summary>Blur behind a menu, clipped to its 12px rounded corners (menus are WPF popups, which are layered windows).</summary>
    public static void GlassMenu(ContextMenu menu)
    {
        if (PresentationSource.FromVisual(menu) is not HwndSource src) return;
        IntPtr h = src.Handle;
        Native.EnableBlur(h);
        double s = Scale(menu);
        int w = (int)Math.Ceiling(menu.ActualWidth * s), ht = (int)Math.Ceiling(menu.ActualHeight * s), r = (int)(24 * s);
        if (w > 0 && ht > 0) Native.SetWindowRgn(h, Native.CreateRoundRectRgn(0, 0, w + 1, ht + 1, r, r), true);
    }

    /// <summary>Activate a launcher window; falls back to forcing it forward when Windows' focus rules refuse (hook hotkeys).</summary>
    public static void BringToFront(Window w)
    {
        if (!w.Activate()) Native.ForceForeground(Hwnd(w));
    }

    public static MenuItem Item(string header, Action action, bool enabled = true, bool isChecked = false, string keys = "")
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled, IsChecked = isChecked, InputGestureText = keys };
        mi.Click += (_, _) => action();
        return mi;
    }

    /// <summary>Build a context menu; null entries become separators (never leading, trailing or doubled).</summary>
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
