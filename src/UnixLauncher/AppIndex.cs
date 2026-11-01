using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace UnixLauncher;

public class AppEntry
{
    public string Name { get; init; } = "";
    /// <summary>Parsing name inside shell:AppsFolder — an AUMID for Store/modern apps, or "{KnownFolder}\path\app.exe".</summary>
    public string ParsingName { get; init; } = "";
    public ImageSource? Icon { get; set; }

    public string Launch => @"shell:AppsFolder\" + ParsingName;
    public string? Exe => ParsingName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? ParsingName : null;
    public string? Aumid => ParsingName.Contains('\\') || ParsingName.Contains("://") ? null : ParsingName;

    public DockPin ToPin() => new() { Name = Name, Launch = Launch, Exe = Exe, Aumid = Aumid };
}

/// <summary>Every installed app (desktop + Store), read from the same "All apps" list the Start menu uses.</summary>
static class AppIndex
{
    public static IReadOnlyList<AppEntry> Apps { get; private set; } = Array.Empty<AppEntry>();
    public static event Action? Loaded;

    public static AppEntry? ByAumid(string aumid) => Apps.FirstOrDefault(a => string.Equals(a.Aumid, aumid, StringComparison.OrdinalIgnoreCase));

    public static void LoadAsync()
    {
        // Shell COM objects want an STA thread; keep the slow icon extraction off the UI thread.
        var t = new Thread(() =>
        {
            var list = new List<AppEntry>();
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                dynamic items = shell.NameSpace("shell:AppsFolder").Items();
                int count = items.Count;
                for (int i = 0; i < count; i++)
                {
                    dynamic item = items.Item(i);
                    string name = item.Name, path = item.Path;
                    if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                    var e = new AppEntry { Name = name, ParsingName = path };
                    e.Icon = Icons.ForApp(e.Name, e.Exe, e.Launch);
                    list.Add(e);
                }
            }
            catch (Exception ex) { App.Log(ex); }
            Apps = list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            Application.Current.Dispatcher.BeginInvoke(() => Loaded?.Invoke());
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }
}
