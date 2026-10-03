using System.IO;
using System.Text.Json;

namespace DynIsland;

public class AppSettings
{
    public bool IslandEnabled { get; set; } = true;
    public bool ShowTime { get; set; } = true;
    public bool ShowWeather { get; set; } = true;
    public bool Use24Hour { get; set; } = false;
    public bool ShowSeconds { get; set; } = false;
    public bool AutoLocation { get; set; } = true;
    public string ManualLocation { get; set; } = "";
    public double ManualLat { get; set; } = double.NaN;
    public double ManualLon { get; set; } = double.NaN;
    public bool UseFahrenheit { get; set; } = false;
    public int WeatherRefreshMinutes { get; set; } = 30;
    public bool MusicEnabled { get; set; } = true;
    public bool ShowArtwork { get; set; } = true;
    public bool ShowControls { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    public int NotificationSeconds { get; set; } = 5;
    public double IslandScale { get; set; } = 1.0;
    public double IslandOpacity { get; set; } = 0.96;
    public double CornerRadius { get; set; } = 24;
    public double AnimationSpeed { get; set; } = 1.0; // 0.5 slow .. 2 fast multiplier inverse
    public double TimerMinutes { get; set; } = 10;
    public DateTime? TimerEndsAtUtc { get; set; } = null;
    public uint HotkeyMods { get; set; } = 0x0002 | 0x0004; // Ctrl+Shift (win32 MOD_CONTROL|MOD_SHIFT)
    public uint HotkeyVk { get; set; } = 0x44; // D (virtual-key code)
}

public static class SettingsStore
{
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynIsland");
    private static readonly string Path_ = System.IO.Path.Combine(Dir, "settings.json");
    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(Path_))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path_)) ?? new();
        }
        catch { Current = new(); }
    }

    public static void Save()
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(Path_, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}
