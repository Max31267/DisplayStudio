using System.Globalization;
using System.Text.Json;

namespace MiniDisplay.Core;

public static class SourceParsers
{
    public static CodexQuota[] Codex(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object) return [];
        var root = response.TryGetProperty("result", out var result) ? result : response;
        if (root.ValueKind != JsonValueKind.Object) return [];
        if (root.TryGetProperty("rateLimitsByLimitId", out var groups) && groups.ValueKind == JsonValueKind.Object)
            return groups.EnumerateObject().Where(group => group.Value.ValueKind == JsonValueKind.Object).Select(group => Quota(group.Name, group.Value)).ToArray();
        return root.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object ? [Quota("codex", legacy)] : [];
    }
    private static CodexQuota Quota(string id, JsonElement data) => new(id, String(data, "limitName") ?? id,
        data.TryGetProperty("primary", out var primary) ? Window(primary) : null, data.TryGetProperty("secondary", out var secondary) ? Window(secondary) : null);
    private static QuotaWindow? Window(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        double? used = Number(data, "usedPercent");
        double? minutes = Number(data, "windowDurationMins"), reset = Number(data, "resetsAt");
        return new(used.HasValue ? Math.Clamp(100 - used.Value, 0, 100) : null,
            minutes is > 0 and <= int.MaxValue ? (int)minutes.Value : null,
            reset is >= -62135596800 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds((long)reset.Value) : null);
    }
    public static string WindowName(int? minutes) => minutes switch
    {
        null => "Окно недоступно", int value when value % 1440 == 0 => $"{value / 1440} д", int value when value % 60 == 0 => $"{value / 60} ч", _ => minutes + " мин"
    };
    public static string ResetName(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is null) return "Сброс: —";
        var remaining = reset.Value - now;
        if (remaining <= TimeSpan.Zero) return "Ожидается обновление";
        return remaining.TotalHours >= 24 ? $"Сброс через {Math.Ceiling(remaining.TotalDays):0} д" : remaining.TotalHours >= 1 ? $"Сброс через {(int)remaining.TotalHours} ч {remaining.Minutes} мин" : $"Сброс через {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} мин";
    }
    public static double? Number(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.TryGetDoubleSafe(out double number) && double.IsFinite(number) ? number : null;
    public static string? String(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool TryGetDoubleSafe(this JsonElement value, out double number) { number = 0; return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number); }
    public static DataReading Reading(double? value, string unit, string format = "0") => value.HasValue && double.IsFinite(value.Value) ? new(value.Value.ToString(format, CultureInfo.GetCultureInfo("ru-RU")) + unit, value) : new("—");
    public static string WeatherDescription(int? code) => code switch
    {
        0 => "Ясно", 1 or 2 => "Малооблачно", 3 => "Облачно", 45 or 48 => "Туман", 51 or 53 or 55 => "Морось", 56 or 57 or 66 or 67 => "Ледяной дождь",
        61 or 63 or 65 or 80 or 81 or 82 => "Дождь", 71 or 73 or 75 or 77 or 85 or 86 => "Снег", 95 or 96 or 99 => "Гроза", _ => "—"
    };
}
