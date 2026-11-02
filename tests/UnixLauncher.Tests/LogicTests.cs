using UnixLauncher;
using Xunit;

public class DockMathTests
{
    static readonly double[] Sizes = { 56, 56, 13, 56 };
    static readonly bool[] Scalable = { true, true, false, true };

    [Fact]
    public void IconUnderCursorGetsFullMagnification()
    {
        var s = DockMath.Scales(Sizes, Scalable, 28, 48, 2.0); // centre of the first icon
        Assert.Equal(2.0, s[0], 6);
        Assert.True(s[1] > 1 && s[1] < s[0]); // neighbour grows less
        Assert.Equal(1, s[2]);                // separators never scale
    }

    [Fact]
    public void FarAwayIconsStayNormal()
    {
        var s = DockMath.Scales(Sizes, Scalable, 10_000, 48, 2.0);
        Assert.All(s, v => Assert.Equal(1, v));
    }
}

public class AppMatchTests
{
    [Fact] public void AumidWinsWhenBothHaveOne() =>
        Assert.False(AppMatch.Matches("chrome.exe", "Chrome", @"C:\x\chrome.exe", "Chrome.Profile2"));

    [Fact] public void ExeNameMatchesAcrossPathForms() =>
        Assert.True(AppMatch.Matches(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Notepad++\notepad++.exe", null, @"C:\Program Files\Notepad++\NOTEPAD++.EXE", null));

    [Fact] public void PinWithoutExeOrAumidNeverMatches() =>
        Assert.False(AppMatch.Matches(null, null, @"C:\Windows\explorer.exe", null));

    [Fact] public void FallsBackToExeWhenWindowHasNoAumid() =>
        Assert.True(AppMatch.Matches(@"C:\Windows\explorer.exe", "Microsoft.Windows.Explorer", @"C:\Windows\explorer.exe", null));
}

public class AppSearchTests
{
    [Fact]
    public void PrefixBeatsWordBeatsSubstring()
    {
        var apps = new[] { "Microsoft Edge", "Edge Dev", "Hedgehog" };
        Assert.Equal(new[] { "Edge Dev", "Microsoft Edge", "Hedgehog" }, AppSearch.Filter(apps, a => a, "edge", 10));
    }

    [Fact] public void EmptyQueryMatchesNothing() => Assert.Empty(AppSearch.Filter(new[] { "Calculator" }, a => a, "  ", 10));
}

public class CalcTests
{
    [Theory]
    [InlineData("2+2", "4")]
    [InlineData("10 / 4", "2.5")]
    [InlineData("(3+4)*2", "14")]
    public void Evaluates(string input, string expected)
    {
        Assert.True(Calc.TryEvaluate(input, out var r));
        Assert.Equal(expected, r);
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("42")]          // a bare number is a search, not a sum
    [InlineData("1/0")]
    [InlineData("2+")]
    public void IgnoresNonArithmetic(string input) => Assert.False(Calc.TryEvaluate(input, out _));
}

public class CommandLineTests
{
    static bool Exists(string p) => p is @"C:\Program Files\App\app.exe" or "rundll32.exe";

    [Fact] public void Quoted() =>
        Assert.Equal((@"C:\Program Files\App\app.exe", "--min"), CommandLine.Split("\"C:\\Program Files\\App\\app.exe\" --min", Exists));

    [Fact] public void UnquotedWithSpacesUsesExistingFile() =>
        Assert.Equal((@"C:\Program Files\App\app.exe", "--min"), CommandLine.Split(@"C:\Program Files\App\app.exe --min", Exists));

    [Fact] public void UnknownFileSplitsAtFirstSpace() =>
        Assert.Equal(("tool.exe", "/s x"), CommandLine.Split("tool.exe /s x", _ => false));
}

public class CalendarMathTests
{
    [Fact]
    public void SundayStartNeedsNoBlankForNov2026() // 1 Nov 2026 is a Sunday
    {
        var c = CalendarMath.Cells(2026, 11, DayOfWeek.Sunday);
        Assert.Equal(1, c[0]);
        Assert.Equal(30, c.Count);
    }

    [Fact]
    public void MondayStartPutsSundayLast()
    {
        var c = CalendarMath.Cells(2026, 11, DayOfWeek.Monday);
        Assert.Equal(6, c.TakeWhile(x => x == null).Count());
        Assert.Equal(36, c.Count);
    }

    [Fact] public void LeapFebruaryHas29Days() => Assert.Equal(29, CalendarMath.Cells(2028, 2, DayOfWeek.Sunday).Max());
}

public class MenuTextTests
{
    [Theory]
    [InlineData("&File", "File")]
    [InlineData("Save &As...\tCtrl+S", "Save As...")]
    [InlineData("Tom && Jerry", "Tom & Jerry")]
    public void Cleans(string raw, string expected) => Assert.Equal(expected, MenuText.Clean(raw));
}

public class FinderLogicTests
{
    [Theory]
    [InlineData(1, "1 byte")]
    [InlineData(820, "820 bytes")]
    [InlineData(1_234, "1.2 KB")]
    [InlineData(999_999, "1 MB")]
    [InlineData(34_000_000_000, "34 GB")]
    public void Sizes(long bytes, string expected)
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try { Assert.Equal(expected, FileFormat.Size(bytes)); }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void Dates()
    {
        var now = new DateTime(2026, 11, 2, 15, 0, 0);
        Assert.StartsWith("Today at ", FileFormat.Date(now.AddHours(-1), now));
        Assert.StartsWith("Yesterday at ", FileFormat.Date(now.AddDays(-1), now));
        Assert.Contains("2025", FileFormat.Date(new DateTime(2025, 3, 1), now));
    }

    [Fact]
    public void NaturalOrder()
    {
        var names = new List<string> { "file10", "File2", "file1", "a", "file02b" };
        names.Sort(NaturalComparer.Instance);
        Assert.Equal(new[] { "a", "file1", "File2", "file02b", "file10" }, names);
    }

    [Theory]
    [InlineData("report.pdf", null)]
    [InlineData("", "empty")]
    [InlineData("a:b", "characters")]
    [InlineData("notes.", "dot")]
    [InlineData("con.txt", "reserves")]
    public void NameProblems(string name, string? fragment)
    {
        var p = FileNames.Problem(name);
        if (fragment == null) Assert.Null(p); else Assert.Contains(fragment, p);
    }

    [Fact]
    public void UniqueAndCopyNames()
    {
        var taken = new HashSet<string> { "untitled folder", "report.pdf", "report copy.pdf" };
        Assert.Equal("untitled folder 2", FileNames.Unique("untitled folder", taken.Contains, false));
        Assert.Equal("report copy 2.pdf", FileNames.CopyName("report.pdf", false, taken.Contains));
        Assert.Equal("v1.2 copy", FileNames.CopyName("v1.2", true, taken.Contains)); // folders keep their dots
    }

    [Fact]
    public void Inside()
    {
        Assert.True(FileNames.IsSameOrInside(@"C:\Users\me\Docs\a", @"C:\Users\me\docs\"));
        Assert.True(FileNames.IsSameOrInside(@"C:\Users\me", @"C:\Users\me"));
        Assert.False(FileNames.IsSameOrInside(@"C:\Users\meow", @"C:\Users\me"));
    }
}

public class WinKeyFilterTests
{
    const int Win = WinKeyFilter.LWin, E = 0x45, D = 0x44, S = 0x53, Shift = 0xA0;

    static WinKeyFilter Filter(bool start = false)
    {
        var f = new WinKeyFilter { ReplaceStart = start };
        f.Combos[E] = "finder";
        f.Combos[S] = "spotlight";
        return f;
    }

    [Fact]
    public void LoneWinTapOpensLaunchpadAndMasksStart()
    {
        var f = Filter(start: true);
        f.Handle(Win, true);
        Assert.Equal(new WinKeyFilter.Result(false, "launchpad", true), f.Handle(Win, false));
    }

    [Fact]
    public void LoneWinTapIsLeftAloneWhenStartNotReplaced()
    {
        var f = Filter();
        f.Handle(Win, true);
        Assert.Equal(default, f.Handle(Win, false));
    }

    [Fact]
    public void WinEIsTakenOverOnceAndStartIsMasked()
    {
        var f = Filter();
        f.Handle(Win, true);
        Assert.Equal(new WinKeyFilter.Result(true, "finder", false), f.Handle(E, true));
        Assert.Equal(new WinKeyFilter.Result(true, null, false), f.Handle(E, true));   // auto-repeat: swallowed, no second window
        Assert.True(f.Handle(E, false).Swallow);                                          // and its release
        Assert.True(f.Handle(Win, false).InjectMask);
    }

    [Fact]
    public void OtherShortcutsPassThrough()
    {
        var f = Filter(start: true);
        f.Handle(Win, true);
        Assert.Equal(default, f.Handle(D, true));     // Win+D isn't ours
        Assert.Equal(default, f.Handle(Win, false));  // Windows saw a combo: no mask, no Launchpad
    }

    [Fact]
    public void WinShiftSStaysWithWindows() // screenshot shortcut must not become Spotlight
    {
        var f = Filter();
        f.Handle(Win, true);
        f.Handle(Shift, true);
        Assert.False(f.Handle(S, true).Swallow);
    }
}
