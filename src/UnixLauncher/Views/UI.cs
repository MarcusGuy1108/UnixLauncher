using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace UnixLauncher.Views;

static class UI
{
    public static IntPtr Hwnd(Window w) => new WindowInteropHelper(w).EnsureHandle();

    /// <summary>Device pixels per DIP for this window (DPI scale).</summary>
    public static double Scale(Visual v) => PresentationSource.FromVisual(v)?.CompositionTarget?.TransformToDevice.M11 ?? 1;

    /// <summary>A user-supplied icon (icons/&lt;name&gt;.png) if present, else a Segoe Fluent/MDL2 glyph.</summary>
    public static FrameworkElement Icon(string overrideName, string glyph, double size)
    {
        if (Icons.Override(overrideName) is { } img) return new Image { Source = img, Width = size, Height = size };
        return new TextBlock
        {
            Text = glyph, FontSize = size, FontFamily = (FontFamily)Application.Current.Resources["GlyphFont"],
            Foreground = (Brush)Application.Current.Resources["Text"], VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    public static MenuItem Item(string header, Action action, bool enabled = true, bool isChecked = false)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled, IsChecked = isChecked };
        mi.Click += (_, _) => action();
        return mi;
    }

    /// <summary>Build a context menu; null entries become separators.</summary>
    public static ContextMenu Menu(params MenuItem?[] items)
    {
        var cm = new ContextMenu();
        foreach (var i in items) cm.Items.Add(i ?? (object)new Separator());
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
    public Popover()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(FontFamilyProperty, "UiFont");
        Deactivated += (_, _) => Hide();
        SourceInitialized += (_, _) => Native.SetToolWindow(UI.Hwnd(this), false);
    }

    public void Toggle() { if (IsVisible) Hide(); else Open(); }

    public void Open()
    {
        OnOpening();
        Show();
        Activate();
    }

    protected virtual void OnOpening() { }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Hide();
        base.OnKeyDown(e);
    }
}

/// <summary>A launcher window with macOS chrome: rounded, red/yellow/green buttons, drag anywhere on the title bar.</summary>
public class MacWindow : Window
{
    Rect? _restore;

    public MacWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(StyleProperty, "MacWindow");
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Close") is Button close) close.Click += (_, _) => Close();
        if (GetTemplateChild("PART_Min") is Button min) min.Click += (_, _) => WindowState = WindowState.Minimized;
        if (GetTemplateChild("PART_Zoom") is Button zoom) zoom.Click += (_, _) => Zoom();
        if (GetTemplateChild("PART_TitleBar") is FrameworkElement bar)
            bar.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Zoom(); else DragMove(); };
    }

    /// <summary>macOS "zoom": fill the work area (borderless WPF maximise would cover the menu bar and dock).</summary>
    void Zoom()
    {
        if (_restore is { } r) { Left = r.Left; Top = r.Top; Width = r.Width; Height = r.Height; _restore = null; return; }
        _restore = new Rect(Left, Top, Width, Height);
        var wa = SystemParameters.WorkArea;
        Left = wa.Left; Top = wa.Top; Width = wa.Width; Height = wa.Height;
    }
}
