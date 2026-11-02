using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UnixLauncher;

public class DockPin
{
    public string Name { get; set; } = "";
    /// <summary>What gets ShellExecuted: a path, a folder, a URI, or shell:AppsFolder\&lt;AUMID&gt;. "::launchpad" is built in.</summary>
    public string Launch { get; set; } = "";
    public string? Exe { get; set; }
    public string? Aumid { get; set; }
}

public class Settings
{
    public string DockPosition { get; set; } = "Bottom"; // Bottom | Left | Right
    public double DockIconSize { get; set; } = 48;
    public bool DockMagnify { get; set; } = true;
    public double DockMagnification { get; set; } = 1.7;
    public bool DockAutoHide { get; set; }
    public bool HideWindowsTaskbar { get; set; } = true;
    public bool TrafficLightOverlay { get; set; } = true;
    /// <summary>Cover Windows' own minimise/maximise/close buttons while the round ones are shown.</summary>
    public bool HideCaptionButtons { get; set; } = true;
    /// <summary>Exe names (e.g. "chrome.exe") whose title bar the traffic-light overlay should leave alone.</summary>
    public List<string> OverlayExclude { get; set; } = new();
    public bool DoNotDisturb { get; set; }
    /// <summary>Finder's view: "Icons" or "List".</summary>
    public string FinderView { get; set; } = "Icons";

    // Windows Integration: each one takes over a built-in Windows feature. All off by default.
    public bool ReplaceExplorer { get; set; }       // folders + Win+E open Finder
    public bool ReplaceStart { get; set; }          // Windows key opens Launchpad
    public bool ReplaceSearch { get; set; }         // Win+S / Win+Q open Spotlight
    public bool ReplaceQuickSettings { get; set; }  // Win+A opens Control Centre
    public bool ReplaceNotifications { get; set; }  // Win+N opens Notification Centre

    /// <summary>Alt works like the Mac's ⌘ key (Settings › Keyboard). Off by default: it changes how Alt behaves everywhere.</summary>
    public bool MacShortcuts { get; set; }

    /// <summary>macOS hot corners: corner ("Top Left"…) → action ("Mission Control"…). Empty = none.</summary>
    public Dictionary<string, string> HotCorners { get; set; } = new();
    public static readonly string[] HotCornerActions =
        { "-", "Mission Control", "Desktop", "Launchpad", "Notification Centre", "Put Display to Sleep", "Lock Screen" };

    /// <summary>macOS 26 "Show menu bar background"; off = fully transparent bar.</summary>
    public bool MenuBarBackground { get; set; } = true;

    /// <summary>Control Centre module → "Show in Menu Bar" | "Show When Active" | "Don't Show in Menu Bar".</summary>
    public Dictionary<string, string> MenuBarItems { get; set; } = new();

    public const string Show = "Show in Menu Bar", WhenActive = "Show When Active", Hide = "Don't Show in Menu Bar";

    /// <summary>Modules that can appear in the menu bar, the choices each offers, and the default.</summary>
    public static readonly (string Name, string[] Modes, string Default)[] MenuBarModules =
    {
        ("Wi-Fi", new[] { Show, Hide }, Show),
        ("Bluetooth", new[] { Show, Hide }, Hide),
        ("Focus", new[] { WhenActive, Hide }, WhenActive),
        ("Sound", new[] { Show, Hide }, Hide),
        ("Now Playing", new[] { WhenActive, Hide }, WhenActive),
        ("Battery", new[] { Show, Hide }, Show),
        ("Spotlight", new[] { Show, Hide }, Show),
    };

    public string MenuBarMode(string module) =>
        MenuBarItems.TryGetValue(module, out var m) ? m : Array.Find(MenuBarModules, x => x.Name == module).Default ?? Hide;
    /// <summary>San Francisco can't be shipped; drop in any installed font family here.</summary>
    /// <summary>San Francisco is used when installed (Apple's free SF Pro download); otherwise Windows' own UI font.</summary>
    public string FontFamily { get; set; } = DefaultFont;
    public const string DefaultFont = "SF Pro Text, SF Pro, Segoe UI Variable Text, Segoe UI";
    public List<DockPin> Pinned { get; set; } = new()
    {
        FinderPin(),
        new() { Name = "Launchpad", Launch = "::launchpad" },
        new() { Name = "Settings", Launch = @"shell:AppsFolder\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
                Aumid = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel" },
        new() { Name = "Downloads", Launch = Sys.Downloads },
    };

    static DockPin FinderPin() => new() { Name = "Finder", Launch = "::finder" };

    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnixLauncher");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Current { get; private set; } = new();
    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath)) Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
            // Earlier versions pinned File Explorer by default; Finder takes that place. Pins the user made stay.
            int i = Current.Pinned.FindIndex(p => p.Name == "File Explorer" && p.Exe == "explorer.exe");
            if (i >= 0 && !Current.Pinned.Any(p => p.Launch == "::finder")) Current.Pinned[i] = FinderPin();
            Sys.FixDownloadsPin(Current.Pinned);
            if (Current.FontFamily == "Segoe UI Variable Text, Segoe UI") Current.FontFamily = DefaultFont; // the old default: pick up SF Pro
        }
        catch (Exception ex)
        {
            App.Log(ex);
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { } // keep the broken file for the user, start fresh
        }
    }

    /// <summary>notify: false for settings nobody else reacts to (saves rebuilding the dock and icons).</summary>
    public static void Save(bool notify = true)
    {
        Directory.CreateDirectory(Dir);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
        File.Move(tmp, FilePath, true); // atomic replace so a crash can't truncate settings
        if (notify) Changed?.Invoke();
    }
}
