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
