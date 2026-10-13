using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;

namespace DynamicIsland;

public sealed record HourForecast(DateTime Time, double Temperature, int Code, bool IsDay);

public sealed class WeatherReport
{
    public string Place { get; init; } = "";
    public double Temperature { get; init; }
    public double FeelsLike { get; init; }
    public double High { get; init; }
    public double Low { get; init; }
    public int Code { get; init; }
    public bool IsDay { get; init; }
    public List<HourForecast> Hours { get; init; } = new();
}

/// <summary>
/// Weather from Open-Meteo (free, no account or key needed). Your location comes from Windows'
/// location setting, or from a town you type in Settings.
/// </summary>
public static class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Looks up a town ("Gravelines", "London") and returns its coordinates and full name.</summary>
    public static async Task<(double Latitude, double Longitude, string Name)?> FindPlaceAsync(string query)
    {
        try
        {
            string url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&name=" + Uri.EscapeDataString(query.Trim());
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
            var first = results[0];
            string name = first.GetProperty("name").GetString() ?? query;
            if (first.TryGetProperty("country", out var country)) name += ", " + country.GetString();
            return (first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble(), name);
        }
        catch { return null; }
    }

    /// <summary>Asks Windows where you are (needs Settings › Privacy › Location turned on for desktop apps).</summary>
    public static async Task<(double Latitude, double Longitude)?> CurrentLocationAsync()
    {
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed) return null;
            var position = await new Geolocator().GetGeopositionAsync(TimeSpan.FromHours(1), TimeSpan.FromSeconds(10));
            var point = position.Coordinate.Point.Position;
            return (point.Latitude, point.Longitude);
        }
        catch { return null; }
    }

    /// <summary>The town name for some coordinates (best effort; a free service that needs no key).</summary>
    public static async Task<string?> NameForAsync(double latitude, double longitude)
    {
        try
        {
            string url = $"https://api.bigdatacloud.net/data/reverse-geocode-client?localityLanguage=en&latitude={Num(latitude)}&longitude={Num(longitude)}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            foreach (var key in new[] { "city", "locality", "principalSubdivision" })
                if (doc.RootElement.TryGetProperty(key, out var value) && value.GetString() is { Length: > 0 } name)
                    return name;
        }
        catch { }
        return null;
    }

    public static async Task<WeatherReport?> GetAsync(double latitude, double longitude, string place, bool fahrenheit)
    {
        try
        {
            string url = "https://api.open-meteo.com/v1/forecast"
                + $"?latitude={Num(latitude)}&longitude={Num(longitude)}"
                + "&current=temperature_2m,apparent_temperature,weather_code,is_day"
                + "&hourly=temperature_2m,weather_code,is_day"
                + "&daily=temperature_2m_max,temperature_2m_min"
                + "&timezone=auto&forecast_days=2"
                + (fahrenheit ? "&temperature_unit=fahrenheit" : "");

            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var root = doc.RootElement;
            var current = root.GetProperty("current");
            var daily = root.GetProperty("daily");
            var hourly = root.GetProperty("hourly");

            // The next five hours, starting from the coming hour.
            var hours = new List<HourForecast>();
            var times = hourly.GetProperty("time");
            var temps = hourly.GetProperty("temperature_2m");
            var codes = hourly.GetProperty("weather_code");
            var days = hourly.GetProperty("is_day");
            DateTime currentTime = DateTime.Parse(current.GetProperty("time").GetString()!, CultureInfo.InvariantCulture);
            for (int i = 0; i < times.GetArrayLength() && hours.Count < 5; i++)
            {
                var time = DateTime.Parse(times[i].GetString()!, CultureInfo.InvariantCulture);
                if (time <= currentTime) continue;
                hours.Add(new HourForecast(time, temps[i].GetDouble(), codes[i].GetInt32(), days[i].GetInt32() == 1));
            }

            return new WeatherReport
            {
                Place = place,
                Temperature = current.GetProperty("temperature_2m").GetDouble(),
                FeelsLike = current.GetProperty("apparent_temperature").GetDouble(),
                Code = current.GetProperty("weather_code").GetInt32(),
                IsDay = current.GetProperty("is_day").GetInt32() == 1,
                High = daily.GetProperty("temperature_2m_max")[0].GetDouble(),
                Low = daily.GetProperty("temperature_2m_min")[0].GetDouble(),
                Hours = hours,
            };
        }
        catch { return null; }
    }

    /// <summary>Words for a WMO weather code.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Cloudy",
        45 or 48 => "Fog",
        51 or 53 or 55 or 56 or 57 => "Drizzle",
        61 or 63 or 66 => "Rain",
        65 or 67 => "Heavy rain",
        71 or 73 or 75 or 77 => "Snow",
        80 or 81 => "Showers",
        82 => "Heavy showers",
        85 or 86 => "Snow showers",
        95 or 96 or 99 => "Thunderstorm",
        _ => "—",
    };

    /// <summary>A simple weather symbol for a code.</summary>
    public static string Symbol(int code, bool isDay) => code switch
    {
        0 or 1 => isDay ? "☀" : "☾",       // ☀ / ☾
        2 => isDay ? "⛅" : "☁",             // ⛅ / ☁
        3 => "☁",                                // ☁
        45 or 48 => "≈",                         // ≈ (fog)
        71 or 73 or 75 or 77 or 85 or 86 => "❄", // ❄
        95 or 96 or 99 => "⛈",                   // ⛈
        _ when code >= 51 => "☂",                // ☂ (rain/drizzle/showers)
        _ => "☁",
    };
}
