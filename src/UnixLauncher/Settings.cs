using System;
using System.Collections.Generic;
using System.IO;
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
    /// <summary>Exe names (e.g. "chrome.exe") whose title bar the traffic-light overlay should leave alone.</summary>
    public List<string> OverlayExclude { get; set; } = new();
    public bool DoNotDisturb { get; set; }
    /// <summary>San Francisco can't be shipped; drop in any installed font family here.</summary>
    public string FontFamily { get; set; } = "Segoe UI Variable Text, Segoe UI";
    public List<DockPin> Pinned { get; set; } = new()
    {
        new() { Name = "File Explorer", Launch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), Exe = "explorer.exe" },
        new() { Name = "Launchpad", Launch = "::launchpad" },
        new() { Name = "Settings", Launch = @"shell:AppsFolder\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
                Aumid = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel" },
        new() { Name = "Downloads", Launch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") },
    };

    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnixLauncher");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Current { get; private set; } = new();
    public static event Action? Changed;

    public static void Load()
    {
        try { if (File.Exists(FilePath)) Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex)
        {
            App.Log(ex);
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { } // keep the broken file for the user, start fresh
        }
    }

    public static void Save()
    {
        Directory.CreateDirectory(Dir);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
        File.Move(tmp, FilePath, true); // atomic replace so a crash can't truncate settings
        Changed?.Invoke();
    }
}
