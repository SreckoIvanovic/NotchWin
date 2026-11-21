using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Windows.Devices.Geolocation;

namespace NotchWin.Services;

public enum WeatherKind { Clear, PartlyCloudy, Cloudy, Fog, Rain, Snow, Thunder }

public sealed class WeatherService
{
    private static readonly HttpClient Http = CreateClient();
    private (double lat, double lon)? _loc;

    public bool HasData { get; private set; }
    public double Temperature { get; private set; }
    public WeatherKind Kind { get; private set; }
    public bool IsDay { get; private set; } = true;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NotchWin/1.0");
        return c;
    }

    public async Task<bool> RefreshAsync(string? city)
    {
        try
        {
            _loc ??= await GetLocationAsync(city);
            if (_loc is not { } loc) return false;

            var url = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0:F2}&longitude={1:F2}&current=temperature_2m,weather_code,is_day",
                loc.lat, loc.lon);
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var cur = doc.RootElement.GetProperty("current");
            Temperature = cur.GetProperty("temperature_2m").GetDouble();
            Kind = FromWmo(cur.GetProperty("weather_code").GetInt32());
            IsDay = cur.GetProperty("is_day").GetInt32() == 1;
            HasData = true;
            return true;
        }
        catch { return false; }
    }

    private static async Task<(double, double)?> GetLocationAsync(string? city)
    {
        if (!string.IsNullOrWhiteSpace(city))
        {
            try
            {
                var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&name=" + Uri.EscapeDataString(city.Trim());
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                if (doc.RootElement.TryGetProperty("results", out var res) && res.GetArrayLength() > 0)
                    return (res[0].GetProperty("latitude").GetDouble(), res[0].GetProperty("longitude").GetDouble());
            }
            catch { }
        }

        try
        {
            if (await Geolocator.RequestAccessAsync() != GeolocationAccessStatus.Allowed) return null;
            var g = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            var p = await g.GetGeopositionAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(15));
            return (p.Coordinate.Point.Position.Latitude, p.Coordinate.Point.Position.Longitude);
        }
        catch { return null; }
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
}
