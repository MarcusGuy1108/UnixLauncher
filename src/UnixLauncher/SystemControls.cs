using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Windows.Devices.Radios;
using Windows.Media.Control;

namespace UnixLauncher;

/// <summary>Master volume of the default output device (Core Audio).</summary>
static class Volume
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        // Vtable order matters; unused slots are declared only to keep the offsets right.
        [PreserveSig] int RegisterControlChangeNotify(IntPtr p);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr p);
        [PreserveSig] int GetChannelCount(out uint c);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    static IAudioEndpointVolume? Endpoint()
    {
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.GetDefaultAudioEndpoint(0, 1, out var dev) != 0) return null; // eRender, eMultimedia
            var iid = typeof(IAudioEndpointVolume).GUID;
            return dev.Activate(ref iid, 0x17, IntPtr.Zero, out var o) == 0 ? (IAudioEndpointVolume)o : null; // CLSCTX_ALL
        }
        catch { return null; } // no audio device
    }

    /// <summary>0–100, or null when there is no output device.</summary>
    public static int? Get() => Endpoint() is { } ep && ep.GetMasterVolumeLevelScalar(out float v) == 0 ? (int)Math.Round(v * 100) : null;

    public static void Set(int percent)
    {
        if (Endpoint() is not { } ep) return;
        var ctx = Guid.Empty;
        ep.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref ctx);
        ep.SetMute(percent == 0, ref ctx);
    }
}

/// <summary>Built-in display brightness (laptops/tablets). External monitors return null.</summary>
static class Brightness
{
    public static int? Get()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in s.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return null;
    }

    public static void Set(int percent)
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in s.Get()) o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)Math.Clamp(percent, 0, 100) });
        }
        catch (Exception ex) { App.Log(ex.Message); }
    }
}

/// <summary>Wi-Fi / Bluetooth radio switches.</summary>
static class Radios
{
    static async Task<Radio?> Find(bool bluetooth)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null;
            var kind = bluetooth ? RadioKind.Bluetooth : RadioKind.WiFi;
            return (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
        }
        catch { return null; }
    }

    /// <summary>null = no such radio / no access.</summary>
    public static async Task<bool?> IsOn(bool bluetooth) => await Find(bluetooth) is { } r ? r.State == RadioState.On : null;

    public static async Task<bool?> Toggle(bool bluetooth)
    {
        if (await Find(bluetooth) is not { } r) return null;
        var target = r.State == RadioState.On ? RadioState.Off : RadioState.On;
        try { await r.SetStateAsync(target); } catch { return null; }
        return target == RadioState.On;
    }
}

/// <summary>Windows light/dark mode for apps and the system.</summary>
static class Appearance
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue("AppsUseLightTheme") is int v && v == 0; }
    }

    public static void SetDark(bool dark)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Key))
        {
            k.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            k.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        }
        // Tell running apps to repaint. 0x2 = SMTO_ABORTIFHUNG.
        Native.SendMessageTimeout(new IntPtr(0xFFFF), Native.WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", 0x2, 200, out _);
    }
}

public record NowPlaying(string Title, string Artist, ImageSource? Art, bool Playing);

/// <summary>Whatever is playing (Spotify, browsers, Media Player…) via the system media controls.</summary>
static class Media
{
    static GlobalSystemMediaTransportControlsSessionManager? _mgr;

    static async Task<GlobalSystemMediaTransportControlsSession?> Session()
    {
        try { _mgr ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); return _mgr.GetCurrentSession(); }
        catch { return null; }
    }

    public static async Task<NowPlaying?> Current()
    {
        try
        {
            if (await Session() is not { } s) return null;
            var p = await s.TryGetMediaPropertiesAsync();
            ImageSource? art = null;
            if (p.Thumbnail != null)
            {
                using var stream = (await p.Thumbnail.OpenReadAsync()).AsStream();
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = stream; bmp.EndInit(); bmp.Freeze();
                art = bmp;
            }
            bool playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            return new NowPlaying(p.Title, p.Artist, art, playing);
        }
        catch { return null; }
    }

    public static async Task PlayPause() { if (await Session() is { } s) await s.TryTogglePlayPauseAsync(); }
    public static async Task Next() { if (await Session() is { } s) await s.TrySkipNextAsync(); }
    public static async Task Previous() { if (await Session() is { } s) await s.TrySkipPreviousAsync(); }
}

static class Network
{
    /// <summary>
    /// Name of the connection in use (for Wi-Fi this is the network name), null when offline.
    /// The profile name rather than the SSID API, which newer Windows gates behind location permission.
    /// </summary>
    public static string? WifiName()
    {
        try { return Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile()?.ProfileName; }
        catch { return null; }
    }
}

static class Battery
{
    /// <summary>(percent, charging), or null on desktops.</summary>
    public static (int percent, bool charging)? Get()
    {
        if (!Native.GetSystemPowerStatus(out var s) || (s.BatteryFlag & 128) != 0 || s.BatteryLifePercent == 255) return null;
        return (s.BatteryLifePercent, s.ACLineStatus == 1);
    }
}
