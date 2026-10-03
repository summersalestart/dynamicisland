using System.Windows;
using System.Windows.Input;

namespace DynIsland;

public partial class SettingsWindow : Window
{
    private readonly MainWindow island;
    public SettingsWindow(MainWindow island)
    {
        this.island = island;
        InitializeComponent();
        var s = SettingsStore.Current;
        C_Enabled.IsChecked = s.IslandEnabled;
        S_Scale.Value = s.IslandScale; S_Opacity.Value = s.IslandOpacity;
        T_Hotkey.Text = MainWindow.HotkeyText(s.HotkeyMods, s.HotkeyVk);
        C_24h.IsChecked = s.Use24Hour; C_Sec.IsChecked = s.ShowSeconds; C_ShowTime.IsChecked = s.ShowTime;
        C_ShowWx.IsChecked = s.ShowWeather; C_AutoLoc.IsChecked = s.AutoLocation; T_Loc.Text = s.ManualLocation;
        C_F.IsChecked = s.UseFahrenheit; T_Refresh.Text = s.WeatherRefreshMinutes.ToString();
        C_Music.IsChecked = s.MusicEnabled; C_Art.IsChecked = s.ShowArtwork; C_Controls.IsChecked = s.ShowControls;
        C_Notif.IsChecked = s.NotificationsEnabled; T_NotifSec.Text = s.NotificationSeconds.ToString();
        T_TimerMin.Text = s.TimerMinutes.ToString();
        // dark checkboxes readability
        foreach (var cb in new System.Windows.Controls.CheckBox[] { C_Enabled, C_24h, C_Sec, C_ShowTime, C_ShowWx, C_AutoLoc, C_F, C_Music, C_Art, C_Controls, C_Notif })
            cb.Foreground = System.Windows.Media.Brushes.White;
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsStore.Current;
        s.IslandEnabled = C_Enabled.IsChecked == true;
        s.IslandScale = S_Scale.Value; s.IslandOpacity = S_Opacity.Value;
        T_HotkeyStatus.Visibility = Visibility.Collapsed;
        if (!MainWindow.TryParseHotkey(T_Hotkey.Text, out uint hm, out uint hk))
        {
            T_HotkeyStatus.Text = "Use e.g. Ctrl+Shift+D (modifiers + letter, digit, or F-key).";
            T_HotkeyStatus.Visibility = Visibility.Visible;
            T_Hotkey.Text = MainWindow.HotkeyText(s.HotkeyMods, s.HotkeyVk);
            return;
        }
        s.HotkeyMods = hm; s.HotkeyVk = hk;
        s.Use24Hour = C_24h.IsChecked == true; s.ShowSeconds = C_Sec.IsChecked == true; s.ShowTime = C_ShowTime.IsChecked == true;
        s.ShowWeather = C_ShowWx.IsChecked == true; s.AutoLocation = C_AutoLoc.IsChecked == true; s.ManualLocation = T_Loc.Text.Trim();
        // Accept either "City Name" or explicit "lat, lon" coordinates.
        s.ManualLat = double.NaN; s.ManualLon = double.NaN;
        if (!s.AutoLocation)
        {
            var parts = s.ManualLocation.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2
                && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lat)
                && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lon)
                && lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180)
            { s.ManualLat = lat; s.ManualLon = lon; }
        }
        s.UseFahrenheit = C_F.IsChecked == true;
        if (int.TryParse(T_Refresh.Text, out int m)) s.WeatherRefreshMinutes = Math.Clamp(m, 5, 180);
        s.MusicEnabled = C_Music.IsChecked == true; s.ShowArtwork = C_Art.IsChecked == true; s.ShowControls = C_Controls.IsChecked == true;
        s.NotificationsEnabled = C_Notif.IsChecked == true;
        if (int.TryParse(T_NotifSec.Text, out int n)) s.NotificationSeconds = Math.Clamp(n, 2, 30);
        SettingsStore.Save();
        island.ApplySettings();
        if (!island.HotkeyActive)
        {
            T_HotkeyStatus.Text = "That hotkey is taken by another app — pick a different one.";
            T_HotkeyStatus.Visibility = Visibility.Visible;
            return;
        }
        Close();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Drag(object sender, MouseButtonEventArgs e)
    {
        try { if (e.ChangedButton == MouseButton.Left) DragMove(); } catch { }
    }
    private void TimerStart_Click(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(T_TimerMin.Text, out double m) && m >= 1 && m <= 1440)
        {
            island.StartTimer(TimeSpan.FromMinutes(m));
            T_TimerStatus.Text = $"Timer running: {m} min.";
        }
        else T_TimerStatus.Text = "Enter 1–1440 minutes.";
    }
    private void TimerCancel_Click(object sender, RoutedEventArgs e)
    {
        island.CancelTimer();
        T_TimerStatus.Text = "Timer cancelled.";
    }
}
