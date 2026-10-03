using System.Net.Http;
using System.Text.Json;

namespace DynIsland;

public record WeatherInfo(double TempC, string Condition, string Icon, string Place);

public static class WeatherCodes
{
    public static (string label, string glyph) Map(int code) => code switch
    {
        0 => ("Clear", "\u2600"),
        1 => ("Mostly Clear", "\u263C"),
        2 => ("Partly Cloudy", "\u26C5"),
        3 => ("Cloudy", "\u2601"),
        45 or 48 => ("Fog", "\u2591"),
        51 or 53 or 55 => ("Drizzle", "\u2614"),
        56 or 57 => ("Freezing Drizzle", "\u2614"),
        61 => ("Light Rain", "\u2614"),
        63 => ("Rain", "\u2614"),
        65 => ("Heavy Rain", "\u2614"),
        66 or 67 => ("Freezing Rain", "\u2614"),
        71 or 73 or 75 or 77 => ("Snow", "\u2744"),
        80 or 81 or 82 => ("Showers", "\u2614"),
        85 or 86 => ("Snow Showers", "\u2744"),
        95 => ("Thunderstorm", "\u26A1"),
        96 or 99 => ("Storm + Hail", "\u26A1"),
        _ => ("—", "\u2601"),
    };
}

public class WeatherService
{
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private WeatherInfo? last;
    private string lastPlace = "";
    public event Action<WeatherInfo>? Updated;
    public WeatherInfo? Last => last;

    public async Task StartAsync(CancellationToken token)
    {
        await RefreshAsync();
        while (!token.IsCancellationRequested)
        {
            int mins = Math.Clamp(SettingsStore.Current.WeatherRefreshMinutes, 5, 180);
            try { await Task.Delay(TimeSpan.FromMinutes(mins), token); } catch { break; }
            await RefreshAsync();
        }
    }

    public async Task RefreshAsync()
    {
        try
        {
            var (lat, lon, place) = await ResolveLocationAsync();
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat:F3}&longitude={lon:F3}&current=temperature_2m,weather_code&timezone=auto";
            var json = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var cur = doc.RootElement.GetProperty("current");
            double t = cur.GetProperty("temperature_2m").GetDouble();
            int code = cur.GetProperty("weather_code").GetInt32();
            var (label, glyph) = WeatherCodes.Map(code);
            lastPlace = place;
            last = new WeatherInfo(t, label, glyph, place);
            Updated?.Invoke(last);
        }
        catch { /* keep last known */ }
    }

    private async Task<(double lat, double lon, string place)> ResolveLocationAsync()
    {
        var s = SettingsStore.Current;
        if (!s.AutoLocation && !double.IsNaN(s.ManualLat))
            return (s.ManualLat, s.ManualLon, string.IsNullOrWhiteSpace(s.ManualLocation) ? $"{s.ManualLat:F1},{s.ManualLon:F1}" : s.ManualLocation);
        if (!s.AutoLocation && !string.IsNullOrWhiteSpace(s.ManualLocation))
        {
            try
            {
                var g = await http.GetStringAsync($"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(s.ManualLocation)}&count=1&language=en&format=json");
                using var d = JsonDocument.Parse(g);
                var r = d.RootElement.GetProperty("results")[0];
                return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(), r.GetProperty("name").GetString() ?? s.ManualLocation);
            }
            catch { }
        }
        // auto via ip-api (coarse, no key)
        try
        {
            var j = await http.GetStringAsync("http://ip-api.com/json/?fields=lat,lon,city,country");
            using var d = JsonDocument.Parse(j);
            double lat = d.RootElement.GetProperty("lat").GetDouble();
            double lon = d.RootElement.GetProperty("lon").GetDouble();
            string city = d.RootElement.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
            return (lat, lon, city);
        }
        catch { }
        return (40.71, -74.0, "New York"); // fallback
    }

    public static string FormatTemp(double c) =>
        SettingsStore.Current.UseFahrenheit ? $"{c * 9 / 5 + 32:F0}°F" : $"{c:F0}°C";
}
