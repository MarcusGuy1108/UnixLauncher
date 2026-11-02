using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace UnixLauncher.Views;

public partial class ControlCentre : Popover
{
    bool _loading;
    readonly DispatcherTimer _brightnessDebounce = new() { Interval = TimeSpan.FromMilliseconds(120) };

    public ControlCentre()
    {
        InitializeComponent();
        // Glyphs inherit their colour so a switched-on circle can turn them blue.
        WifiTile.Tag = UI.Icon("wifi", "", 16, inheritColor: true);
        BtTile.Tag = UI.Icon("bluetooth", "", 18, inheritColor: true);
        ShareBtn.Tag = UI.Icon("nearby-sharing", "", 18, inheritColor: true);
        FocusTile.Tag = UI.Icon("focus", "", 16, inheritColor: true);
        TaskViewBtn.Tag = UI.Icon("task-view", "", 18, inheritColor: true);
        MirrorBtn.Tag = UI.Icon("screen-mirroring", "", 18, inheritColor: true);
        DarkTile.Tag = UI.Icon("dark-mode", "◐", 18, inheritColor: true);
        CalcBtn.Tag = UI.Icon("calculator", "", 18, inheritColor: true);
        TimerBtn.Tag = UI.Icon("timer", "", 18, inheritColor: true);
        ShotBtn.Tag = UI.Icon("screenshot", "", 18, inheritColor: true);
        OutputBtn.Tag = UI.Icon("sound-output", "", 12, inheritColor: true);
        SunSmall.Content = UI.Icon("", "", 11);
        SunBig.Content = UI.Icon("", "", 16);
        SpeakerSmall.Content = UI.Icon("", "", 11);
        SpeakerBig.Content = UI.Icon("", "", 16);
        PrevBtn.Content = UI.Icon("media-previous", "", 14);
        NextBtn.Content = UI.Icon("media-next", "", 14);
        PlayBtn.Content = UI.Icon("media-play", "", 16);

        VolumeSlider.ValueChanged += (_, e) => { if (!_loading) Volume.Set((int)e.NewValue); };
        // WMI brightness calls are slow; apply once the slider settles, off the UI thread.
        _brightnessDebounce.Tick += (_, _) =>
        {
            _brightnessDebounce.Stop();
            int v = (int)BrightnessSlider.Value;
            Task.Run(() => Brightness.Set(v));
        };
        BrightnessSlider.ValueChanged += (_, _) => { if (!_loading) { _brightnessDebounce.Stop(); _brightnessDebounce.Start(); } };
    }

    protected override void OnOpening()
    {
        Left = SystemParameters.PrimaryScreenWidth - Width - 12;
        Top = 34; // just under the menu bar
        _ = Load();
    }

    async Task Load()
    {
        _loading = true;
        FocusTile.IsChecked = Settings.Current.DoNotDisturb;
        FocusSub.Text = Settings.Current.DoNotDisturb ? "On" : "Off";
        DarkTile.IsChecked = Appearance.IsDark;

        var vol = Volume.Get();
        SoundCard.Visibility = vol == null ? Visibility.Collapsed : Visibility.Visible;
        VolumeSlider.Value = vol ?? 0;
        var bright = await Task.Run(Brightness.Get);
        DisplayCard.Visibility = bright == null ? Visibility.Collapsed : Visibility.Visible;
        BrightnessSlider.Value = bright ?? 0;
        _loading = false;

        ShowWifi(await Radios.IsOn(false));
        ShowRadio(BtTile, await Radios.IsOn(true));
        await LoadMedia();
    }

    static void ShowRadio(ToggleButton tile, bool? on)
    {
        tile.IsChecked = on == true;
        tile.IsEnabled = on != null;
    }

    void ShowWifi(bool? on)
    {
        ShowRadio(WifiTile, on);
        WifiSub.Text = on == null ? "Unavailable" : on.Value ? Network.WifiName() ?? "Not Connected" : "Off"; // macOS shows the network name
    }

    async Task LoadMedia()
    {
        var np = await Media.Current();
        MediaTitle.Text = np?.Title is { Length: > 0 } t ? t : "Not Playing";
        MediaArtist.Text = np?.Artist ?? "";
        MediaArt.Source = np?.Art;
        bool playing = np?.Playing == true;
        PlayBtn.Content = UI.Icon(playing ? "media-pause" : "media-play", playing ? "" : "", 16);
        PrevBtn.IsEnabled = PlayBtn.IsEnabled = NextBtn.IsEnabled = np != null;
    }

    async void Wifi_Click(object sender, RoutedEventArgs e) => ShowWifi(await Radios.Toggle(false));
    async void Bluetooth_Click(object sender, RoutedEventArgs e) => ShowRadio(BtTile, await Radios.Toggle(true));

    void Focus_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.DoNotDisturb = FocusTile.IsChecked == true;
        FocusSub.Text = Settings.Current.DoNotDisturb ? "On" : "Off";
        Settings.Save();
    }

    void Dark_Click(object sender, RoutedEventArgs e) => Appearance.SetDark(DarkTile.IsChecked == true);

    /// <summary>Close the panel, then do something that opens another window or Windows UI.</summary>
    void Then(Action action)
    {
        Hide();
        action();
    }

    void Share_Click(object sender, RoutedEventArgs e) => Then(() => Sys.Launch("ms-settings:crossdevice"));
    void TaskView_Click(object sender, RoutedEventArgs e) => Then(() => Native.PressKeys(0x5B, 0x09));          // Win+Tab
    void Mirror_Click(object sender, RoutedEventArgs e) => Then(() => Native.PressKeys(0x5B, 0x4B));            // Win+K: Cast
    void Output_Click(object sender, RoutedEventArgs e) => Then(() => Sys.Launch("ms-settings:sound"));
    void Calculator_Click(object sender, RoutedEventArgs e) => Then(() => Sys.Launch("calc.exe"));
    void Timer_Click(object sender, RoutedEventArgs e) => Then(() => Sys.Launch("ms-clock:"));
    void Screenshot_Click(object sender, RoutedEventArgs e) => Then(() => Native.PressKeys(0x5B, 0x10, 0x53));  // Win+Shift+S
    void Edit_Click(object sender, RoutedEventArgs e) => Then(() => SettingsWindow.Open("Control Centre"));

    void OpenSettings(object sender, MouseButtonEventArgs e)
    {
        if (sender is ButtonBase { CommandParameter: string uri }) Then(() => Sys.Launch(uri));
    }

    async void Prev_Click(object sender, RoutedEventArgs e) { await Media.Previous(); await LoadMedia(); }
    async void Play_Click(object sender, RoutedEventArgs e) { await Media.PlayPause(); await Task.Delay(300); await LoadMedia(); }
    async void Next_Click(object sender, RoutedEventArgs e) { await Media.Next(); await Task.Delay(300); await LoadMedia(); }
}
