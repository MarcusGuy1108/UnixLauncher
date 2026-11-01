using System;
using System.Windows;
using Microsoft.Win32;

namespace UnixLauncher.Views;

public partial class AboutWindow : MacWindow
{
    public AboutWindow()
    {
        InitializeComponent();
        Logo.Content = UI.Icon("about-logo", "", 72); // E7F8 = DeviceLaptopNoPic

        using var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string product = cv?.GetValue("ProductName") as string ?? "Windows";
        string build = cv?.GetValue("CurrentBuildNumber") as string ?? "";
        // Windows 11 still reports "Windows 10" in ProductName; the build number tells them apart.
        if (int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
        Product.Text = product;
        Version.Text = cv?.GetValue("DisplayVersion") is string display ? $"Version {display} (build {build})" : $"Build {build}";

        DeviceName.Text = Environment.MachineName;
        using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        Cpu.Text = (cpu?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Unknown";
        Memory.Text = $"{Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0)} GB";
    }

    void MoreInfo_Click(object sender, RoutedEventArgs e) => Sys.Launch("ms-settings:about");
}
