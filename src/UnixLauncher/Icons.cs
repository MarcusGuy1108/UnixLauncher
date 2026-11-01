using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UnixLauncher;

/// <summary>
/// Icon lookup. User-supplied files win, so any macOS-style artwork can be dropped in without code
/// changes (see icons/README.md). Search order for a name such as "apps/chrome":
///   %AppData%\UnixLauncher\icons\apps\chrome.png|.ico, then &lt;exe folder&gt;\icons\apps\chrome.png|.ico.
/// </summary>
static class Icons
{
    public static readonly string UserDir = Path.Combine(Settings.Dir, "icons");
    static readonly string[] Dirs = { UserDir, Path.Combine(AppContext.BaseDirectory, "icons") };
    static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static void ClearCache() => Cache.Clear();

    public static ImageSource? Override(string name) => Cache.GetOrAdd("o|" + name, _ =>
    {
        foreach (var dir in Dirs)
            foreach (var ext in new[] { ".png", ".ico" })
            {
                var path = Path.Combine(dir, name + ext);
                if (!File.Exists(path)) continue;
                try
                {
                    // Largest frame, so multi-size .ico files look sharp in the magnified dock.
                    var frame = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad)
                                             .Frames.OrderByDescending(f => f.PixelWidth).First();
                    frame.Freeze();
                    return frame;
                }
                catch (Exception ex) { App.Log($"Icon {path}: {ex.Message}"); }
            }
        return null;
    });

    static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>Icon for an app: icons/apps/&lt;exe name&gt;, then icons/apps/&lt;display name&gt;, then the shell's own icon.</summary>
    public static ImageSource? ForApp(string name, string? exe, string shellPath)
    {
        var exeName = exe == null ? "" : Path.GetFileNameWithoutExtension(exe);
        return (exeName.Length > 0 ? Override("apps/" + Safe(exeName)) : null)
               ?? (name.Length > 0 ? Override("apps/" + Safe(name)) : null)
               ?? Shell(shellPath);
    }

    public static ImageSource? Shell(string parsingName) => Cache.GetOrAdd("s|" + parsingName, _ => ShellUncached(parsingName));

    /// <summary>
    /// The icon Explorer shows for a path, a folder, or shell:AppsFolder\&lt;AUMID&gt; (Store apps).
    /// Call on an STA thread.
    /// </summary>
    public static ImageSource? ShellUncached(string parsingName)
    {
        try
        {
            var iid = typeof(Native.IShellItemImageFactory).GUID;
            Native.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory);
            try
            {
                // 0x5 = SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY (no file thumbnails)
                if (factory.GetImage(new Native.SIZE { cx = 128, cy = 128 }, 0x5, out var hbmp) != 0) return null;
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally { Native.DeleteObject(hbmp); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch { return null; } // not a shell item; caller falls back to a glyph
    }
}
