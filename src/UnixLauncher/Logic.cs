using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UnixLauncher;

// Pure logic with no WPF/Win32 dependencies, so it is unit-tested on any OS.

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

public static class MenuText
{
    /// <summary>Win32 menu caption → display text: drop the shortcut after a tab and the &amp; accelerator marks.</summary>
    public static string Clean(string raw)
    {
        int tab = raw.IndexOf('\t');
        if (tab >= 0) raw = raw[..tab];
        return raw.Replace("&&", "\0").Replace("&", "").Replace("\0", "&").Trim();
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
