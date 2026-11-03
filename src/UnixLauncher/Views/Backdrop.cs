using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UnixLauncher.Views;

/// <summary>
/// macOS-style glass without Windows' blur, which ignores rounded shapes on see-through windows (blur poked out past every
/// corner). Instead: a copy of the screen behind, blurred and colour-boosted here, painted inside the shape by WPF, so corners
/// are exact. A plain screen copy leaves out see-through (layered) windows, so our own panels never end up in their own glass.
/// shortcut: a still picture; panels refresh it each time they open and the dock every second; live blur would need DWM.
/// </summary>
static class Backdrop
{
    const int Shrink = 4; // blur at a quarter size: faster, and the upscale smooths it further

    /// <summary>The blurred screen inside <paramref name="r"/> (screen pixels), pulled toward <paramref name="tint"/> by <paramref name="amount"/>.</summary>
    public static BitmapSource? Capture(Native.RECT r, Color tint, double amount)
    {
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= Shrink || h <= Shrink) return null;
        IntPtr screen = Native.GetDC(IntPtr.Zero), mem = Native.CreateCompatibleDC(screen), bmp = Native.CreateCompatibleBitmap(screen, w, h);
        IntPtr old = Native.SelectObject(mem, bmp);
        try
        {
            if (!Native.BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, 0x00CC0020)) return null; // SRCCOPY (no CAPTUREBLT: skips layered windows)
            Native.SelectObject(mem, old);
            old = IntPtr.Zero;
            return Blur(Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()), tint, amount);
        }
        catch (Exception ex) { App.Log(ex); return null; }
        finally
        {
            if (old != IntPtr.Zero) Native.SelectObject(mem, old);
            Native.DeleteObject(bmp);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Any picture made into the same glass: blurred at a quarter size, colour-boosted, pulled toward the tint.</summary>
    public static BitmapSource Blur(BitmapSource full, Color tint, double amount)
    {
        var small = new FormatConvertedBitmap(new TransformedBitmap(full, new ScaleTransform(1.0 / Shrink, 1.0 / Shrink)), PixelFormats.Bgra32, null, 0);
        int sw = small.PixelWidth, sh = small.PixelHeight, stride = sw * 4;
        var px = new byte[stride * sh];
        small.CopyPixels(px, stride, 0);
        for (int i = 0; i < 3; i++) { BoxBlur(px, sw, sh, 3, true); BoxBlur(px, sw, sh, 3, false); } // three boxes ≈ a gaussian
        Vibrancy(px, tint, amount);
        var result = BitmapSource.Create(sw, sh, 96, 96, PixelFormats.Bgra32, null, px, stride);
        result.Freeze();
        return result;
    }

    /// <summary>The desktop picture as glass (Mission Control): the wallpaper alone, without the windows on it.</summary>
    public static ImageBrush? Wallpaper(Color tint, double amount)
    {
        // shortcut: one picture filling the screen; slideshows show the current one, Fit/Tile/Span layouts aren't copied.
        string? path = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(path);
            img.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file open
            img.DecodePixelWidth = 1600; // blurred anyway: no need for every pixel of a 4K picture
            img.EndInit();
            return new ImageBrush(Blur(img, tint, amount)) { Stretch = Stretch.UniformToFill };
        }
        catch (Exception ex) { App.Log(ex); return null; } // no wallpaper (solid colour) or unreadable
    }

    /// <summary>The same as a brush that fills its element (a Border clips it to its rounded corners).</summary>
    public static ImageBrush? Brush(Native.RECT r, Color tint, double amount) =>
        Capture(r, tint, amount) is { } img ? new ImageBrush(img) { Stretch = Stretch.Fill } : null;

    /// <summary>Screen rectangle of an element, in pixels.</summary>
    public static Native.RECT ScreenRect(FrameworkElement el)
    {
        var a = el.PointToScreen(new Point(0, 0));
        var b = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
        return new Native.RECT { Left = (int)a.X, Top = (int)a.Y, Right = (int)Math.Ceiling(b.X), Bottom = (int)Math.Ceiling(b.Y) };
    }

    /// <summary>One pass of a box blur along rows or columns, edges clamped.</summary>
    static void BoxBlur(byte[] px, int w, int h, int radius, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        var line = new int[len * 3];
        for (int l = 0; l < lines; l++)
        {
            int Index(int i) => horizontal ? (l * w + i) * 4 : (i * w + l) * 4;
            for (int i = 0; i < len; i++) { int p = Index(i); line[i * 3] = px[p]; line[i * 3 + 1] = px[p + 1]; line[i * 3 + 2] = px[p + 2]; }
            int n = radius * 2 + 1;
            for (int c = 0; c < 3; c++)
            {
                int sum = 0;
                for (int k = -radius; k <= radius; k++) sum += line[Math.Clamp(k, 0, len - 1) * 3 + c];
                for (int i = 0; i < len; i++)
                {
                    px[Index(i) + c] = (byte)(sum / n);
                    sum += line[Math.Min(i + radius + 1, len - 1) * 3 + c] - line[Math.Max(i - radius, 0) * 3 + c];
                }
            }
        }
    }

    /// <summary>macOS vibrancy: richer colour, then a pull toward the panel's tint so text stays readable.</summary>
    static void Vibrancy(byte[] px, Color tint, double amount)
    {
        const double saturate = 1.35, maxLuma = 100; // bright wallpaper (sky, snow) gets darker, not greyer: white text stays readable
        for (int i = 0; i < px.Length; i += 4)
        {
            double b = px[i], g = px[i + 1], r = px[i + 2];
            double luma = 0.299 * r + 0.587 * g + 0.114 * b;
            r = luma + (r - luma) * saturate; g = luma + (g - luma) * saturate; b = luma + (b - luma) * saturate;
            b += (tint.B - b) * amount; g += (tint.G - g) * amount; r += (tint.R - r) * amount;
            double k = Math.Min(1, maxLuma / Math.Max(1, 0.299 * r + 0.587 * g + 0.114 * b));
            px[i] = (byte)Math.Clamp(b * k, 0, 255);
            px[i + 1] = (byte)Math.Clamp(g * k, 0, 255);
            px[i + 2] = (byte)Math.Clamp(r * k, 0, 255);
            px[i + 3] = 255;
        }
    }
}
