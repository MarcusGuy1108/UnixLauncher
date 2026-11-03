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
    [InlineData("Ctrl+Shift+Esc", "⇧⌘⎋")]
    [InlineData("Ctrl+W", "⌘W")]
    [InlineData("Alt+Enter", "⌘↩")]
    [InlineData("Win+L", "⌥L")]
    [InlineData("Delete", "⌦")]
    [InlineData("Win", "⊞")]
    [InlineData("Ctrl+[", "⌘[")]
    [InlineData("", "")]
    public void MacKeys(string keys, string expected) => Assert.Equal(expected, MenuText.MacKeys(keys));

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

public class WinKeyFilterGameTests
{
    [Fact]
    public void DisabledFilterLeavesWinKeysToWindows() // a full-screen game is in front
    {
        var f = new WinKeyFilter { ReplaceStart = true, Enabled = false };
        f.Combos[0x45] = "finder";
        f.Handle(WinKeyFilter.LWin, true);
        Assert.Equal(default, f.Handle(0x45, true));
        Assert.Equal(default, f.Handle(WinKeyFilter.LWin, false));
    }
}

public class MacKeyFilterTests
{
    const int Alt = MacKeyFilter.LAlt, C = 'C', Q = 'Q', Tab = 0x09, Shift = 0xA0, Ctrl = 0xA2, Left = 0x25, Z = 'Z';
    static MacKeyFilter On() => new() { Enabled = true };
    static MacKeyFilter.Key D(int vk) => new(vk, false);
    static MacKeyFilter.Key U(int vk) => new(vk, true);

    [Fact]
    public void AltCSendsCtrlCAndWindowsNeverSeesAlt()
    {
        var f = On();
        Assert.True(f.Handle(Alt, true).Swallow);
        var r = f.Handle(C, true);
        Assert.True(r.Swallow);
        Assert.Equal(new[] { D(Ctrl), D(C), U(C), U(Ctrl) }, r.Send);
        Assert.True(f.Handle(C, false).Swallow);
        Assert.True(f.Handle(Alt, false).Swallow);
    }

    [Fact]
    public void AltTabStillWorks() // not a Mac shortcut: Alt is replayed in front of Tab
    {
        var f = On();
        f.Handle(Alt, true);
        var r = f.Handle(Tab, true);
        Assert.True(r.Swallow);
        Assert.Equal(new[] { D(Alt), D(Tab) }, r.Send);
        Assert.False(f.Handle(Tab, false).Swallow);
        Assert.False(f.Handle(Tab, true).Swallow);    // further Tabs while held pass straight through
        Assert.False(f.Handle(Alt, false).Swallow);   // Windows saw the press, so it sees the release
    }

    [Fact]
    public void CtrlUpOpensMissionControl() // ⌃↑, as on a Mac
    {
        var f = On();
        f.Handle(Ctrl, true);
        Assert.Equal(new MacKeyFilter.Result(true, null, "missioncontrol"), f.Handle(0x26, true));
        Assert.True(f.Handle(0x26, false).Swallow);
    }

    [Fact]
    public void CtrlUpStaysWithAppsWhenMacShortcutsAreOff()
    {
        var f = new MacKeyFilter();
        f.Handle(Ctrl, true);
        Assert.Equal(default, f.Handle(0x26, true));
    }

    [Fact]
    public void LoneAltTapDoesNothing() => Assert.True(Tap(On()));

    static bool Tap(MacKeyFilter f) => f.Handle(Alt, true).Swallow && f.Handle(Alt, false).Swallow;

    [Fact]
    public void QuitAndArrows()
    {
        var f = On();
        f.Handle(Alt, true);
        Assert.Equal("quit", f.Handle(Q, true).Action);
        Assert.Equal(new[] { D(0x24), U(0x24) }, f.Handle(Left, true).Send); // Home
    }

    [Fact]
    public void ShiftZIsRedo()
    {
        var f = On();
        f.Handle(Shift, true);
        f.Handle(Alt, true);
        Assert.Equal(new[] { U(Shift), D(Ctrl), D('Y'), U('Y'), U(Ctrl), D(Shift) }, f.Handle(Z, true).Send);
    }

    [Fact]
    public void CtrlAltCombosAreLeftAlone() // e.g. Ctrl+Alt+Del, AltGr on some layouts
    {
        var f = On();
        f.Handle(Ctrl, true);
        Assert.False(f.Handle(Alt, true).Swallow);
        Assert.Equal(default, f.Handle(C, true));
    }

    [Fact]
    public void OffMeansPlainAlt()
    {
        var f = new MacKeyFilter { Enabled = false };
        Assert.False(f.Handle(Alt, true).Swallow);
        Assert.Equal(default, f.Handle(C, true));
        Assert.False(f.Handle(Alt, false).Swallow);
    }

    [Fact]
    public void RightAltIsNeverTouched() => Assert.Equal(default, On().Handle(0xA5, true));

    [Fact]
    public void AltEnterStaysFullScreenToggle() // games and video players use it
    {
        var f = On();
        f.Handle(Alt, true);
        Assert.Equal(new[] { D(Alt), D(0x0D) }, f.Handle(0x0D, true).Send); // replayed as a real Alt+Enter
    }
}

public class HotCornerTests
{
    [Theory]
    [InlineData(0, 0, "Top Left")]
    [InlineData(1919, 0, "Top Right")]
    [InlineData(0, 1079, "Bottom Left")]
    [InlineData(1919, 1079, "Bottom Right")]
    [InlineData(5, 0, null)]
    [InlineData(2500, 0, null)] // a second monitor to the right
    public void Corners(int x, int y, string? expected) => Assert.Equal(expected, HotCornerTrigger.At(x, y, 0, 0, 1920, 1080));

    [Fact]
    public void FiresOncePerVisit()
    {
        var t = new HotCornerTrigger();
        Assert.Equal("Top Left", t.Update("Top Left"));
        Assert.Null(t.Update("Top Left"));   // still there
        Assert.Null(t.Update(null));          // left the corner
        Assert.Equal("Top Left", t.Update("Top Left"));
    }
}

public class OptionKeyTests
{
    const int Win = WinKeyFilter.LWin, Left = 0x25, Shift = 0xA0, Ctrl = 0xA2;
    static MacKeyFilter.Key D(int vk) => new(vk, false);
    static MacKeyFilter.Key U(int vk) => new(vk, true);

    [Fact]
    public void WinLeftJumpsAWordWithoutWindowsSeeingWin()
    {
        var f = new WinKeyFilter { OptionKeys = true };
        f.Handle(Win, true);
        var r = f.Handle(Left, true);
        Assert.True(r.Swallow);
        Assert.Equal(new[] { D(0xE8), U(0xE8), U(Win), D(Ctrl), D(Left), U(Left), U(Ctrl), D(Win) }, r.Send);
        Assert.NotNull(f.Handle(Left, true).Send);    // held: keeps jumping
        Assert.True(f.Handle(Left, false).Swallow);
        Assert.True(f.Handle(Win, false).InjectMask); // releasing Win doesn't open Start
    }

    [Fact]
    public void ShiftStillSelects()
    {
        var f = new WinKeyFilter { OptionKeys = true };
        f.Handle(Win, true);
        f.Handle(Shift, true);
        Assert.True(f.Handle(Left, true).Swallow);
    }

    [Fact]
    public void OffLeavesWindowsSnapping() // Win+← snaps the window when Mac shortcuts are off
    {
        var f = new WinKeyFilter();
        f.Handle(Win, true);
        Assert.Equal(default, f.Handle(Left, true));
    }
}
