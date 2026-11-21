using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Windows.Devices.Geolocation;

namespace NotchWin.Services;

public enum WeatherKind { Clear, PartlyCloudy, Cloudy, Fog, Rain, Snow, Thunder }

public sealed record HourForecast(DateTime Time, double Temp, WeatherKind Kind, bool IsDay);

public sealed class WeatherService
{
    private static readonly HttpClient Http = CreateClient();
    private (double lat, double lon)? _loc;

    public bool HasData { get; private set; }
    public string City { get; private set; } = "";
    public double Temperature { get; private set; }
    public WeatherKind Kind { get; private set; }
    public string Condition { get; private set; } = "";
    public bool IsDay { get; private set; } = true;
    public double High { get; private set; }
    public double Low { get; private set; }
    public int Humidity { get; private set; }
    public double WindKmh { get; private set; }
    public double UvIndex { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public List<HourForecast> Hourly { get; private set; } = new();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NotchWin/1.0 (personal desktop widget)");
        return c;
    }

    public async Task<bool> RefreshAsync(string? city)
    {
        try
        {
            if (_loc == null) await ResolveLocationAsync(city);
            if (_loc is not { } loc) return false;

            var url = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0:F2}&longitude={1:F2}" +
                "&current=temperature_2m,weather_code,is_day,relative_humidity_2m,wind_speed_10m" +
                "&hourly=temperature_2m,weather_code,is_day" +
                "&daily=temperature_2m_max,temperature_2m_min,uv_index_max" +
                "&timezone=auto&forecast_days=2",
                loc.lat, loc.lon);
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var root = doc.RootElement;

            var cur = root.GetProperty("current");
            Temperature = cur.GetProperty("temperature_2m").GetDouble();
            int code = cur.GetProperty("weather_code").GetInt32();
            Kind = FromWmo(code);
            Condition = Describe(code);
            IsDay = cur.GetProperty("is_day").GetInt32() == 1;
            Humidity = (int)Math.Round(cur.GetProperty("relative_humidity_2m").GetDouble());
            WindKmh = cur.GetProperty("wind_speed_10m").GetDouble();
            var nowLocal = DateTime.Parse(cur.GetProperty("time").GetString()!, CultureInfo.InvariantCulture);

            var daily = root.GetProperty("daily");
            High = daily.GetProperty("temperature_2m_max")[0].GetDouble();
            Low = daily.GetProperty("temperature_2m_min")[0].GetDouble();
            UvIndex = daily.GetProperty("uv_index_max")[0].ValueKind == JsonValueKind.Number
                ? daily.GetProperty("uv_index_max")[0].GetDouble() : 0;

            var hourly = root.GetProperty("hourly");
            var times = hourly.GetProperty("time");
            var temps = hourly.GetProperty("temperature_2m");
            var codes = hourly.GetProperty("weather_code");
            var days = hourly.GetProperty("is_day");
            var list = new List<HourForecast>();
            var thisHour = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, nowLocal.Hour, 0, 0);
            for (int i = 0; i < times.GetArrayLength() && list.Count < 6; i++)
            {
                var t = DateTime.Parse(times[i].GetString()!, CultureInfo.InvariantCulture);
                if (t < thisHour) continue;
                list.Add(new HourForecast(t, temps[i].GetDouble(), FromWmo(codes[i].GetInt32()), days[i].GetInt32() == 1));
            }
            Hourly = list;

            UpdatedAt = DateTime.Now;
            HasData = true;
            return true;
        }
        catch { return false; }
    }

    private async Task ResolveLocationAsync(string? city)
    {
        if (!string.IsNullOrWhiteSpace(city))
        {
            try
            {
                var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&name=" + Uri.EscapeDataString(city.Trim());
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                if (doc.RootElement.TryGetProperty("results", out var res) && res.GetArrayLength() > 0)
                {
                    _loc = (res[0].GetProperty("latitude").GetDouble(), res[0].GetProperty("longitude").GetDouble());
                    City = res[0].GetProperty("name").GetString() ?? city.Trim();
                    return;
                }
            }
            catch { }
        }

        try
        {
            if (await Geolocator.RequestAccessAsync() != GeolocationAccessStatus.Allowed) return;
            var g = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            var p = await g.GetGeopositionAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(15));
            _loc = (p.Coordinate.Point.Position.Latitude, p.Coordinate.Point.Position.Longitude);
            City = await ReverseGeocodeAsync(_loc.Value) ?? "My Location";
        }
        catch { }
    }

    private static async Task<string?> ReverseGeocodeAsync((double lat, double lon) loc)
    {
        try
        {
            var url = string.Format(CultureInfo.InvariantCulture,
                "https://nominatim.openstreetmap.org/reverse?format=json&zoom=10&accept-language=en&lat={0:F2}&lon={1:F2}",
                loc.lat, loc.lon);
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("address", out var a)) return null;
            foreach (var key in new[] { "city", "town", "village", "municipality", "county" })
                if (a.TryGetProperty(key, out var v) && v.GetString() is { Length: > 0 } s) return s;
        }
        catch { }
        return null;
    }

    private static WeatherKind FromWmo(int code) => code switch
    {
        0 => WeatherKind.Clear,
        1 or 2 => WeatherKind.PartlyCloudy,
        3 => WeatherKind.Cloudy,
        45 or 48 => WeatherKind.Fog,
        >= 51 and <= 67 => WeatherKind.Rain,
        >= 80 and <= 82 => WeatherKind.Rain,
        >= 71 and <= 77 => WeatherKind.Snow,
        85 or 86 => WeatherKind.Snow,
        >= 95 => WeatherKind.Thunder,
        _ => WeatherKind.Cloudy,
    };

    private static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly Clear",
        2 => "Partly Cloudy",
        3 => "Cloudy",
        45 or 48 => "Fog",
        >= 51 and <= 57 => "Drizzle",
        66 or 67 => "Freezing Rain",
        >= 61 and <= 65 => "Rain",
        >= 80 and <= 82 => "Rain Showers",
        >= 71 and <= 77 => "Snow",
        85 or 86 => "Snow Showers",
        >= 95 => "Thunderstorm",
        _ => "Cloudy",
    };
}
