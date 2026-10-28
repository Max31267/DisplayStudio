using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace MiniDisplay.Core;

public sealed record WeatherPlace(string Name, double Latitude, double Longitude)
{
    public override string ToString() => Name;
}

public sealed record WeatherForecast(double Temperature, double? Humidity, double? FeelsLike, bool EstimatedFeelsLike,
    int? Code, string Summary, string Provider, DateTimeOffset ValidAt)
{
    public Dictionary<DataField, DataReading> Readings(string city)
    {
        var feels = SourceParsers.Reading(FeelsLike, "°C", "0.#");
        if (EstimatedFeelsLike && feels.Number.HasValue) feels = feels with { Text = "≈" + feels.Text };
        return new()
        {
            [DataField.WeatherCity] = new(city.Split(',')[0]),
            [DataField.WeatherTemperature] = SourceParsers.Reading(Temperature, "°C", "0.#"),
            [DataField.WeatherFeelsLike] = feels,
            [DataField.WeatherHumidity] = SourceParsers.Reading(Humidity, "%"),
            [DataField.WeatherSummary] = new(Summary),
            [DataField.WeatherSource] = new(Provider)
        };
    }
}

// Independent HTTPS providers; TLS validation is handled by the platform and never bypassed.
public sealed class WeatherClient(HttpClient http)
{
    public async Task<WeatherForecast> Fetch(double latitude, double longitude, DateTimeOffset now, CancellationToken cancellation)
    {
        if (!double.IsFinite(latitude) || Math.Abs(latitude) > 90 || !double.IsFinite(longitude) || Math.Abs(longitude) > 180)
            throw new ArgumentOutOfRangeException(nameof(latitude));
        string lat = latitude.ToString("0.0000", CultureInfo.InvariantCulture), lon = longitude.ToString("0.0000", CultureInfo.InvariantCulture);
        Exception? failure = null;
        foreach (bool met in new[] { false, true })
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                string url = met
                    ? $"https://api.met.no/weatherapi/locationforecast/2.0/compact?lat={lat}&lon={lon}"
                    : $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code&timeformat=unixtime&timezone=GMT&forecast_days=1";
                using var json = await GetJson(url, cancellation);
                return met ? ParseMet(json.RootElement, now) : ParseOpenMeteo(json.RootElement, now);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException && !cancellation.IsCancellationRequested)
            { failure = ex; }
        }
        throw new IOException("Оба источника погоды недоступны.", failure);
    }

    public async Task<WeatherPlace[]> SearchCity(string query, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 160) return [];
        using var json = await GetJson("https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(query.Trim()) + "&count=8&language=ru&format=json", cancellation);
        if (!json.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return [];
        return results.EnumerateArray().Select(item =>
        {
            double? latitude = SourceParsers.Number(item, "latitude"), longitude = SourceParsers.Number(item, "longitude");
            string name = string.Join(", ", new[] { SourceParsers.String(item, "name"), SourceParsers.String(item, "admin1"), SourceParsers.String(item, "country") }.Where(text => !string.IsNullOrWhiteSpace(text)));
            return latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180 && name.Length is > 0 and <= 160 ? new WeatherPlace(name, latitude.Value, longitude.Value) : null;
        }).OfType<WeatherPlace>().ToArray();
    }

    private async Task<JsonDocument> GetJson(string url, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("DisplayStudio/0.3 (Windows desktop weather display)");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException("Слишком большой ответ погоды.");
        using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream(); byte[] buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (output.Length + count > 1024 * 1024) throw new InvalidDataException("Слишком большой ответ погоды.");
            output.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }

    private static WeatherForecast ParseOpenMeteo(JsonElement root, DateTimeOffset now)
    {
        var current = Child(root, "current");
        double? temperature = InRange(current, "temperature_2m", -100, 80), unix = SourceParsers.Number(current, "time");
        if (temperature is null || unix is null or < -62135596800 or > 253402300799) throw new InvalidDataException("Нет актуальной температуры Open-Meteo.");
        var at = DateTimeOffset.FromUnixTimeSeconds((long)unix.Value); RequireCurrent(at, now);
        double? raw = SourceParsers.Number(current, "weather_code"); int? code = raw is >= 0 and <= 99 && raw == Math.Truncate(raw.Value) ? (int)raw.Value : null;
        string summary = SourceParsers.WeatherDescription(code);
        return new(temperature.Value, InRange(current, "relative_humidity_2m", 0, 100), InRange(current, "apparent_temperature", -150, 100), false,
            summary == "—" ? null : code, summary, "Open-Meteo", at);
    }

    private static WeatherForecast ParseMet(JsonElement root, DateTimeOffset now)
    {
        var series = Child(Child(root, "properties"), "timeseries");
        if (series.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Нет прогноза MET Norway.");
        JsonElement nearest = default; DateTimeOffset at = default; double distance = double.MaxValue;
        foreach (var item in series.EnumerateArray())
        {
            if (!DateTimeOffset.TryParse(SourceParsers.String(item, "time"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)) continue;
            double delta = Math.Abs((time - now).TotalSeconds);
            if (delta < distance) { nearest = item; at = time; distance = delta; }
        }
        RequireCurrent(at, now);
        var data = Child(nearest, "data"); var instant = Child(Child(data, "instant"), "details");
        double? temperature = InRange(instant, "air_temperature", -100, 80), humidity = InRange(instant, "relative_humidity", 0, 100), wind = InRange(instant, "wind_speed", 0, 150);
        if (temperature is null) throw new InvalidDataException("Нет температуры MET Norway.");
        string? symbol = null;
        foreach (string period in new[] { "next_1_hours", "next_6_hours", "next_12_hours" })
            if ((symbol = SourceParsers.String(Child(Child(data, period), "summary"), "symbol_code")) is not null) break;
        int? code = MetCode(symbol); string summary = symbol?.Contains("sleet", StringComparison.Ordinal) == true ? "Дождь со снегом" : SourceParsers.WeatherDescription(code);
        // BoM's apparent-temperature approximation in shade, using humidity and wind (m/s).
        double? feels = humidity.HasValue && wind.HasValue
            ? temperature + 0.33 * (humidity / 100 * 6.105 * Math.Exp(17.27 * temperature.Value / (237.7 + temperature.Value))) - 0.7 * wind - 4 : null;
        return new(temperature.Value, humidity, feels, true, code, summary, "MET Norway", at);
    }

    private static JsonElement Child(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? value : default;
    private static double? InRange(JsonElement parent, string name, double min, double max) => SourceParsers.Number(parent, name) is double value && value >= min && value <= max ? value : null;
    private static void RequireCurrent(DateTimeOffset at, DateTimeOffset now)
    { if (Math.Abs((at - now).TotalHours) > 2) throw new InvalidDataException("Источник вернул устаревшую погоду."); }
    private static int? MetCode(string? symbol)
    {
        if (symbol is null) return null;
        if (symbol.Contains("thunder", StringComparison.Ordinal)) return 95;
        if (symbol.Contains("sleet", StringComparison.Ordinal)) return 67;
        if (symbol.Contains("snow", StringComparison.Ordinal)) return 71;
        if (symbol.Contains("rain", StringComparison.Ordinal)) return 63;
        return symbol.Split('_')[0] switch { "clearsky" => 0, "fair" => 1, "partlycloudy" => 2, "cloudy" => 3, "fog" => 45, _ => null };
    }
}
