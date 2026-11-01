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
        WifiTile.Tag = UI.Icon("wifi", "", 14);
        BtTile.Tag = UI.Icon("bluetooth", "", 14);
        FocusTile.Tag = UI.Icon("focus", "", 14);
        DarkTile.Tag = UI.Icon("dark-mode", "◐", 14);
        PrevBtn.Content = UI.Icon("media-previous", "", 14);
        NextBtn.Content = UI.Icon("media-next", "", 14);

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
        Left = SystemParameters.PrimaryScreenWidth - Width - 4;
        Top = 26;
        _ = Load();
    }

    async Task Load()
    {
        _loading = true;
        FocusTile.IsChecked = Settings.Current.DoNotDisturb;
        FocusSub.Text = Settings.Current.DoNotDisturb ? "On" : "Off";
        DarkTile.IsChecked = Appearance.IsDark;
        DarkSub.Text = Appearance.IsDark ? "On" : "Off";

        var vol = Volume.Get();
        SoundCard.Visibility = vol == null ? Visibility.Collapsed : Visibility.Visible;
        VolumeSlider.Value = vol ?? 0;
        var bright = await Task.Run(Brightness.Get);
        DisplayCard.Visibility = bright == null ? Visibility.Collapsed : Visibility.Visible;
        BrightnessSlider.Value = bright ?? 0;
        _loading = false;

        ShowRadio(WifiTile, WifiSub, await Radios.IsOn(false));
        ShowRadio(BtTile, BtSub, await Radios.IsOn(true));
        await LoadMedia();
    }

    static void ShowRadio(ToggleButton tile, System.Windows.Controls.TextBlock sub, bool? on)
    {
        tile.IsChecked = on == true;
        tile.IsEnabled = on != null;
        sub.Text = on == null ? "Unavailable" : on.Value ? "On" : "Off";
    }

    async Task LoadMedia()
    {
        var np = await Media.Current();
        MediaCard.Visibility = np == null ? Visibility.Collapsed : Visibility.Visible;
        if (np == null) return;
        MediaTitle.Text = np.Title;
        MediaArtist.Text = np.Artist;
        MediaArt.Source = np.Art;
        PlayBtn.Content = UI.Icon(np.Playing ? "media-pause" : "media-play", np.Playing ? "" : "", 14);
    }

    async void Wifi_Click(object sender, RoutedEventArgs e) => ShowRadio(WifiTile, WifiSub, await Radios.Toggle(false));
    async void Bluetooth_Click(object sender, RoutedEventArgs e) => ShowRadio(BtTile, BtSub, await Radios.Toggle(true));

    void Focus_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.DoNotDisturb = FocusTile.IsChecked == true;
        FocusSub.Text = Settings.Current.DoNotDisturb ? "On" : "Off";
        Settings.Save();
    }

    void Dark_Click(object sender, RoutedEventArgs e)
    {
        Appearance.SetDark(DarkTile.IsChecked == true);
        DarkSub.Text = DarkTile.IsChecked == true ? "On" : "Off";
    }

    void OpenSettings(object sender, MouseButtonEventArgs e)
    {
        if (sender is ButtonBase { CommandParameter: string uri }) { Hide(); Sys.Launch(uri); }
    }

    async void Prev_Click(object sender, RoutedEventArgs e) { await Media.Previous(); await LoadMedia(); }
    async void Play_Click(object sender, RoutedEventArgs e) { await Media.PlayPause(); await Task.Delay(300); await LoadMedia(); }
    async void Next_Click(object sender, RoutedEventArgs e) { await Media.Next(); await Task.Delay(300); await LoadMedia(); }
}
