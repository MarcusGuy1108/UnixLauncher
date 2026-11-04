using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UnixLauncher;

// Pure logic with no WPF/Win32 dependencies, so it is unit-tested on any OS.

public class DockPin
{
    public string Name { get; set; } = "";
    /// <summary>What gets ShellExecuted: a path, a folder, a URI, or shell:AppsFolder\&lt;AUMID&gt;. "::launchpad" is built in.</summary>
    public string Launch { get; set; } = "";
    public string? Exe { get; set; }
    public string? Aumid { get; set; }

    public static DockPin Finder() => new() { Name = "Finder", Launch = "::finder" };

    /// <summary>Pins saved by earlier versions: the default File Explorer pin becomes Finder, Launchpad is now Apps. The user's own pins stay.</summary>
    public static void Migrate(List<DockPin> pins)
    {
        int i = pins.FindIndex(p => p.Name == "File Explorer" && p.Exe == "explorer.exe");
        if (i >= 0 && !pins.Any(p => p.Launch == "::finder")) pins[i] = Finder();
        foreach (var p in pins) if (p.Launch == "::launchpad" && p.Name == "Launchpad") p.Name = "Apps";
    }
}

public static class DockMath
{
    /// <summary>
    /// macOS-style magnification. Distances are measured in the *unmagnified* layout so icons
    /// don't jitter as they grow. <paramref name="mouse"/> is the cursor offset from the start
    /// of the unmagnified stack along the dock axis.
    /// </summary>
    public static double[] Scales(IReadOnlyList<double> baseSizes, IReadOnlyList<bool> scalable, double mouse, double iconSize, double maxScale)
    {
        var result = new double[baseSizes.Count];
        double range = iconSize * 2.5, pos = 0;
        for (int i = 0; i < baseSizes.Count; i++)
        {
            double centre = pos + baseSizes[i] / 2;
            pos += baseSizes[i];
            double d = Math.Abs(mouse - centre);
            result[i] = !scalable[i] || d >= range ? 1 : 1 + (maxScale - 1) * Math.Cos(d / range * Math.PI / 2);
        }
        return result;
    }
}

public static class AppMatch
{
    /// <summary>Does a running window belong to a dock pin? AUMID wins, else same exe file name.</summary>
    public static bool Matches(string? pinExe, string? pinAumid, string? winExe, string? winAumid)
    {
        if (pinAumid != null && winAumid != null) return string.Equals(pinAumid, winAumid, StringComparison.OrdinalIgnoreCase);
        if (pinExe == null || winExe == null) return false;
        return string.Equals(FileName(pinExe), FileName(winExe), StringComparison.OrdinalIgnoreCase);
    }

    // Not Path.GetFileName: Windows paths must split on '\' even when tests run on Linux.
    static string FileName(string path) => path[(path.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];

    /// <summary>Grouping key for running windows, the same way the Windows taskbar groups them.</summary>
    public static string Key(string? exe, string? aumid) => (aumid ?? exe ?? "?").ToLowerInvariant();
}

public static class AppSearch
{
    /// <summary>Lower is better; -1 = no match. Prefix of name, then prefix of a word, then substring.</summary>
    public static int Rank(string name, string query)
    {
        if (query.Length == 0) return -1;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0;
        int i = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return -1;
        return char.IsWhiteSpace(name[i - 1]) || name[i - 1] == '-' ? 1 : 2;
    }

    public static List<T> Filter<T>(IEnumerable<T> items, Func<T, string> name, string query, int max) =>
        items.Select(x => (x, r: Rank(name(x), query.Trim())))
             .Where(t => t.r >= 0)
             .OrderBy(t => t.r).ThenBy(t => name(t.x), StringComparer.OrdinalIgnoreCase)
             .Take(max).Select(t => t.x).ToList();
}

public static class CommandLine
{
    /// <summary>
    /// Split a Run-key command into (file, args) the way CreateProcess does: honour quotes, and for
    /// unquoted paths with spaces take the shortest prefix that is an existing file.
    /// </summary>
    public static (string file, string args) Split(string cmd, Func<string, bool> fileExists)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            return end < 0 ? (cmd.Trim('"'), "") : (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        for (int i = cmd.IndexOf(' '); i > 0; i = cmd.IndexOf(' ', i + 1))
            if (fileExists(cmd[..i])) return (cmd[..i], cmd[(i + 1)..].Trim());
        if (fileExists(cmd)) return (cmd, "");
        int sp = cmd.IndexOf(' ');
        return sp < 0 ? (cmd, "") : (cmd[..sp], cmd[(sp + 1)..].Trim());
    }
}

public static class FileFormat
{
    /// <summary>Finder-style size with decimal units (1 KB = 1000 bytes): "820 bytes", "1.2 MB", "34 GB".</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1000) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        string[] units = { "KB", "MB", "GB", "TB", "PB" };
        double v = bytes;
        int u = -1;
        do { v /= 1000; u++; } while (v >= 999.5 && u < units.Length - 1);
        return v.ToString(v < 9.95 ? "0.#" : "0", CultureInfo.CurrentCulture) + " " + units[u];
    }

    /// <summary>Finder-style date: "Today at 14:32", "Yesterday at 09:10", otherwise "2 Nov 2026 at 14:32".</summary>
    public static string Date(DateTime t, DateTime now)
    {
        string time = t.ToString("t", CultureInfo.CurrentCulture);
        if (t.Date == now.Date) return $"Today at {time}";
        if (t.Date == now.Date.AddDays(-1)) return $"Yesterday at {time}";
        return $"{t.ToString("d MMM yyyy", CultureInfo.CurrentCulture)} at {time}";
    }
}

/// <summary>"file2" before "file10", ignoring case — the order Finder and Explorer both use.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        if (a == null || b == null) return string.CompareOrdinal(a, b);
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                // Compare as numbers of any length: drop leading zeros, then longer = bigger, then digit by digit.
                var na = a.AsSpan(si, i - si).TrimStart('0');
                var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : na.SequenceCompareTo(nb);
                if (c != 0) return c;
            }
            else
            {
                int c = string.Compare(a, i, b, j, 1, StringComparison.CurrentCultureIgnoreCase);
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}

public static class FileNames
{
    static readonly char[] Invalid = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
    static readonly string[] Reserved =
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
          "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    /// <summary>Why Windows won't accept this file name, or null if it's fine.</summary>
    public static string? Problem(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "The name can't be empty.";
        if (name.IndexOfAny(Invalid) >= 0 || name.Any(c => c < 32)) return "A name can't contain any of these characters: \\ / : * ? \" < > |";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "A name can't end with a dot or a space.";
        if (name.Length > 255) return "That name is too long.";
        string stem = name.Split('.')[0].TrimEnd(); // "CON.txt" is reserved too
        if (Reserved.Contains(stem.ToUpperInvariant())) return $"\"{stem}\" is a name Windows reserves.";
        return null;
    }

    /// <summary>First free name: "untitled folder", "untitled folder 2", … (number goes before the extension).</summary>
    public static string Unique(string desired, Func<string, bool> taken, bool keepExtension)
    {
        if (!taken(desired)) return desired;
        string stem = keepExtension ? Path.GetFileNameWithoutExtension(desired) : desired;
        string ext = keepExtension ? Path.GetExtension(desired) : "";
        for (int n = 2; ; n++)
        {
            string candidate = $"{stem} {n}{ext}";
            if (!taken(candidate)) return candidate;
        }
    }

    /// <summary>Finder's duplicate naming: "report copy.pdf", then "report copy 2.pdf".</summary>
    public static string CopyName(string name, bool isDir, Func<string, bool> taken) =>
        isDir ? Unique($"{name} copy", taken, false)
              : Unique($"{Path.GetFileNameWithoutExtension(name)} copy{Path.GetExtension(name)}", taken, true);

    /// <summary>Is <paramref name="path"/> the folder itself or somewhere inside it? (Windows paths, case-insensitive.)</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        static string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\');
        string a = Norm(path), b = Norm(folder);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Decides what to do with each key event so Windows-key shortcuts can be taken over (Win+E → Finder, Win alone →
/// Launchpad…). Windows opens Start when it sees Win pressed and released with nothing in between, so when we
/// swallow the other key (or want the lone Win ourselves) we ask for an unused "mask" key to be injected first.
/// </summary>
public sealed class WinKeyFilter
{
    public const int LWin = 0x5B, RWin = 0x5C;
    static readonly int[] Modifiers = { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 }; // Shift, Ctrl, Alt (either side)

    /// <summary>Virtual key pressed with Win (and no Shift/Ctrl/Alt) → action name.</summary>
    public Dictionary<int, string> Combos { get; } = new();
    /// <summary>A lone Win tap gives the "launchpad" action instead of opening Start.</summary>
    public bool ReplaceStart { get; set; }
    /// <summary>Read when Win goes down. Off (a full-screen game or an excluded app is in front): Win keys stay Windows'.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Mac mode: Win sits where ⌥ is, so Win+←/→/⌫/⌦ (± Shift) move or delete by word like ⌥ does on a Mac.</summary>
    public bool OptionKeys { get; set; }

    public readonly record struct Result(bool Swallow, string? Action, bool InjectMask, MacKeyFilter.Key[]? Send = null);

    const int LShift = 0xA0, RShift = 0xA1, AnyShift = 0x10, Ctrl = 0xA2, Mask = 0xE8;
    bool _winHeld, _active, _passedOther, _swallowedOther;
    int _winVk = LWin;
    readonly HashSet<int> _swallowed = new(), _modsHeld = new();

    public Result Handle(int vk, bool down)
    {
        if (vk is LWin or RWin)
        {
            if (down)
            {
                if (!_winHeld) { _winHeld = true; _winVk = vk; _active = Enabled; _passedOther = _swallowedOther = false; } // ignore auto-repeat
                return default;
            }
            _winHeld = false;
            if (!_active || _passedOther) return default;                       // Windows saw a real Win+key combo: no Start anyway
            if (_swallowedOther) return new(false, null, true);     // it only saw Win: mask it so Start stays shut
            return ReplaceStart ? new(false, "launchpad", true) : default;
        }
        if (Array.IndexOf(Modifiers, vk) >= 0)
        {
            if (down) _modsHeld.Add(vk); else _modsHeld.Remove(vk);
            if (_winHeld && down) _passedOther = true;
            return default;
        }
        if (!down) return new(_swallowed.Remove(vk), null, false); // swallow the release of a press we swallowed
        if (!_winHeld) return default;
        if (_active && OptionKeys && vk is 0x25 or 0x27 or 0x08 or 0x2E && _modsHeld.All(m => m is AnyShift or LShift or RShift))
        {
            // Windows must not see Win with these (Win+← snaps the window): mask it, let go of it, send Ctrl+key, press it again.
            _swallowedOther = true;
            _swallowed.Add(vk);
            static MacKeyFilter.Key K(int v, bool up) => new(v, up);
            return new(true, null, false, new[] { K(Mask, false), K(Mask, true), K(_winVk == RWin ? RWin : LWin, true),
                K(Ctrl, false), K(vk, false), K(vk, true), K(Ctrl, true), K(_winVk == RWin ? RWin : LWin, false) });
        }
        if (_active && _modsHeld.Count == 0 && Combos.TryGetValue(vk, out var action))
        {
            _swallowedOther = true;
            bool repeat = !_swallowed.Add(vk);
            return new(true, repeat ? null : action, false); // held key auto-repeats: act once
        }
        _passedOther = true;
        return default;
    }
}

/// <summary>
/// Alt works like the Mac's ⌘ key (it sits in the same place, next to the space bar): Alt+C sends Ctrl+C, Alt+Q quits
/// the app, Alt+← goes to the start of the line, and so on. Windows never sees the Alt press unless the next key isn't a
/// Mac shortcut (Alt+Tab, Alt+F4, Alt+Space…); then Alt is replayed in front of that key so it works as usual.
/// </summary>
public sealed class MacKeyFilter
{
    public const int LAlt = 0xA4; // left Alt only: right Alt is AltGr on many layouts and types characters
    const int Shift = 0xA0, Ctrl = 0xA2, LWin = 0x5B, Home = 0x24, End = 0x23, Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28,
              Back = 0x08, Snapshot = 0x2C, Tab = 0x09, Esc = 0x1B; // Alt+Enter is left alone: it's full screen in games and video players

    public readonly record struct Key(int Vk, bool Up);
    /// <summary>Swallow the real key event; Send = keys to inject instead; Action = something to do ("quit", "hide"…).</summary>
    public readonly record struct Result(bool Swallow, Key[]? Send, string? Action);

    /// <summary>Read when Alt goes down. Off (setting off, a full-screen game or an excluded app in front): Alt is plain Alt.</summary>
    public bool Enabled { get; set; }

    bool _alt, _altShown, _shift, _ctrl, _win;
    bool _switching; // ⌘Tab app switcher is up: it takes the keys until ⌘ is let go
    readonly HashSet<int> _swallowed = new();

    public Result Handle(int vk, bool down)
    {
        switch (vk)
        {
            case LAlt:
                if (down && !_alt)
                {
                    _alt = true;
                    _altShown = !Enabled || _ctrl || _win; // Ctrl+Alt+…, Win+Alt+… stay Windows shortcuts
                }
                else if (!down)
                {
                    _alt = false;
                    if (_switching) { _switching = false; return new(true, null, "switcher-done"); } // let go of ⌘: switch
                }
                return new(!_altShown, null, null); // an Alt Windows never saw: hide its release too (so no menu-bar focus)
            case 0x10 or 0xA0 or 0xA1: _shift = down; return default;
            case 0x11 or 0xA2 or 0xA3: _ctrl = down; return default;
            case 0x5B or 0x5C: _win = down; return default;
        }
        if (!down) return new(_swallowed.Remove(vk), null, null);
        if (vk == Up && _ctrl && !_alt && !_win && !_shift && Enabled) { _swallowed.Add(vk); return new(true, null, "missioncontrol"); } // ⌃↑
        if (!_alt || _altShown || _ctrl || _win) return default;
        if (vk == Tab || _switching) // ⌘Tab / ⌘⇧Tab step through apps; ← → too, Esc closes the switcher
        {
            _swallowed.Add(vk);
            string? step = vk switch { Tab => _shift ? "switcher-prev" : "switcher-next", Left => "switcher-prev", Right => "switcher-next", Esc => "switcher-cancel", _ => null };
            _switching = step != null ? step != "switcher-cancel" : _switching;
            return new(true, null, step);
        }
        if (Map(vk) is { } mac) { _swallowed.Add(vk); return mac; }
        // Not a Mac shortcut: let Windows see Alt after all, in front of this key.
        _altShown = true;
        return new(true, new[] { D(LAlt), D(vk) }, null);
    }

    static Key D(int vk) => new(vk, false);
    static Key U(int vk) => new(vk, true);
    static Result Send(params Key[] keys) => new(true, keys, null);
    static Result WithCtrl(int vk) => Send(D(Ctrl), D(vk), U(vk), U(Ctrl));

    Result? Map(int vk) => vk switch
    {
        'Q' => new(true, null, "quit"),
        'H' => new(true, null, "hide"),
        'M' => new(true, null, "minimise"),
        0xC0 => new(true, null, "next-window"),                                       // ⌘` : next window of this app
        'Z' when _shift => Send(U(Shift), D(Ctrl), D('Y'), U('Y'), U(Ctrl), D(Shift)),  // ⌘⇧Z redo
        '3' when _shift => Send(U(Shift), D(LWin), D(Snapshot), U(Snapshot), U(LWin), D(Shift)), // ⌘⇧3 whole screen (saved to Pictures)
        '4' or '5' when _shift => Send(D(LWin), D('S'), U('S'), U(LWin)),             // ⌘⇧4/5 pick an area (Shift is already down)
        Left => Send(D(Home), U(Home)),                                               // ⌘← / ⌘→ line start / end (⇧ selects)
        Right => Send(D(End), U(End)),
        Up => WithCtrl(Home),                                                          // ⌘↑ / ⌘↓ top / bottom
        Down => WithCtrl(End),
        Back => Send(D(Shift), D(Home), U(Home), U(Shift), D(Back), U(Back)),        // ⌘⌫ delete to line start
        0xDB => Send(D(LAlt), D(Left), U(Left), U(LAlt)),                             // ⌘[ / ⌘] back / forward
        0xDD => Send(D(LAlt), D(Right), U(Right), U(LAlt)),
        (>= 'A' and <= 'Z') or (>= '0' and <= '9') or 0xBA or 0xBB or 0xBC or 0xBD or 0xBE or 0xBF or 0xDC or 0xDE
            => WithCtrl(vk),                                                           // ⌘C ⌘V ⌘T ⌘1 ⌘= ⌘/ … → Ctrl+same
        _ => null,
    };
}

/// <summary>macOS hot corners: fire once when the pointer reaches a screen corner, again only after it has left.</summary>
public sealed class HotCornerTrigger
{
    bool _armed = true;

    /// <summary>The corner the point is in, if it's exactly in one (bounds are left/top inclusive, right/bottom exclusive).</summary>
    public static string? At(int x, int y, int left, int top, int right, int bottom)
    {
        if (x < left || y < top || x >= right || y >= bottom) return null; // on another monitor
        bool l = x == left, t = y == top, r = x == right - 1, b = y == bottom - 1;
        return (l, t, r, b) switch
        {
            (true, true, _, _) => "Top Left",
            (_, true, true, _) => "Top Right",
            (true, _, _, true) => "Bottom Left",
            (_, _, true, true) => "Bottom Right",
            _ => null,
        };
    }

    public string? Update(string? corner)
    {
        if (corner == null) { _armed = true; return null; }
        if (!_armed) return null;
        _armed = false;
        return corner;
    }
}

public static class MenuText
{
    /// <summary>Win32 menu caption → display text: drop the shortcut after a tab and the &amp; accelerator marks.</summary>
    public static string Clean(string raw)
    {
        int tab = raw.IndexOf('\t');
        if (tab >= 0) raw = raw[..tab];
        return raw.Replace("&&", "\0").Replace("&", "").Replace("\0", "&").Trim();
    }

    /// <summary>
    /// "Ctrl+Shift+Esc" → "⇧⌘⎋", the way macOS menus write shortcuts. With Alt as ⌘, both Ctrl and Alt shortcuts are
    /// pressed with the ⌘ key; Win is where ⌥ sits. Modifiers come in macOS order (⌥ ⇧ ⌘) with no plus signs.
    /// </summary>
    public static string MacKeys(string keys)
    {
        if (keys.Length == 0) return keys;
        var parts = keys.Split('+');
        string key = parts[^1], mods = "";
        var held = parts[..^1];
        if (held.Contains("Win")) mods += "⌥";
        if (held.Contains("Shift")) mods += "⇧";
        if (held.Contains("Ctrl") || held.Contains("Alt")) mods += "⌘";
        return mods + key switch
        {
            "Esc" => "⎋", "Enter" => "↩", "Delete" => "⌦", "Backspace" => "⌫", "Tab" => "⇥",
            "Left" => "←", "Right" => "→", "Up" => "↑", "Down" => "↓", "Win" => "⊞", // a lone Windows-key tap
            _ => key,
        };
    }
}

public static class CalendarMath
{
    /// <summary>Month grid cells: leading blanks (null) so day 1 falls under its weekday, then 1..last day.</summary>
    public static List<int?> Cells(int year, int month, DayOfWeek firstDayOfWeek)
    {
        int lead = ((int)new DateTime(year, month, 1).DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        var cells = Enumerable.Repeat<int?>(null, lead).ToList();
        for (int d = 1; d <= DateTime.DaysInMonth(year, month); d++) cells.Add(d);
        return cells;
    }
}

public static class Calc
{
    static readonly Regex Arithmetic = new(@"^[\d\s.+\-*/()%]+$");

    /// <summary>Spotlight-style inline calculator. Only plain arithmetic is evaluated.</summary>
    public static bool TryEvaluate(string input, out string result)
    {
        result = "";
        input = input.Trim();
        if (input.Length < 2 || !Arithmetic.IsMatch(input) || !input.Any(c => "+-*/%".Contains(c))) return false;
        try
        {
            var value = Convert.ToDouble(new DataTable().Compute(input, null), CultureInfo.InvariantCulture);
            if (double.IsNaN(value) || double.IsInfinity(value)) return false;
            result = Math.Round(value, 10).ToString("G12", CultureInfo.InvariantCulture);
            return true;
        }
        catch { return false; }
    }
}
