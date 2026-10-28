namespace MiniDisplay.Core;

public enum DataField
{
    CpuLoad, GpuLoad, RamLoad, CpuTemperature, GpuTemperature, RamUsed, RamTotal,
    WeatherCity, WeatherTemperature, WeatherFeelsLike, WeatherHumidity, WeatherSummary,
    MusicTitle, MusicArtist, MusicAlbum, MusicState, MusicPosition, MusicDuration, MusicProgress,
    CodexPrimaryRemaining, CodexSecondaryRemaining, CodexPrimaryReset, CodexSecondaryReset, CodexPrimaryWindow, CodexSecondaryWindow,
    WeatherSource
}
public sealed record SourceSettings
{
    public string WeatherCity { get; init; } = "";
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string MusicSourceId { get; init; } = "";
    public bool ShowNewTrack { get; init; } = true;
    public string CodexLimitId { get; init; } = "codex";
    public void Validate()
    {
        if (WeatherCity is null || WeatherCity.Length > 160 || MusicSourceId is null || MusicSourceId.Length > 500 || CodexLimitId is null || CodexLimitId.Length > 160)
            throw new InvalidDataException("Некорректные настройки источников.");
        if (Latitude.HasValue != Longitude.HasValue || Latitude is double lat && (!double.IsFinite(lat) || Math.Abs(lat) > 90) || Longitude is double lon && (!double.IsFinite(lon) || Math.Abs(lon) > 180))
            throw new InvalidDataException("Некорректные координаты города.");
    }
}
public sealed record DataReading(string Text, double? Number = null);
public static class DataFields
{
    public static bool IsNumeric(DataField field) => field <= DataField.RamTotal || field is DataField.WeatherTemperature or DataField.WeatherFeelsLike or DataField.WeatherHumidity or DataField.MusicPosition or DataField.MusicDuration or DataField.MusicProgress or DataField.CodexPrimaryRemaining or DataField.CodexSecondaryRemaining;
}
public sealed record SourceOption(string Id, string Label) { public override string ToString() => Label; }
public sealed record QuotaWindow(double? Remaining, int? Minutes, DateTimeOffset? ResetAt);
public sealed record CodexQuota(string Id, string Name, QuotaWindow? Primary, QuotaWindow? Secondary);
public sealed record SourceSnapshot
{
    public IReadOnlyDictionary<DataField, DataReading> Values { get; init; } = new Dictionary<DataField, DataReading>();
    public IReadOnlyDictionary<DataField, double[]> History { get; init; } = new Dictionary<DataField, double[]>();
    public byte[]? Artwork { get; init; }
    public string TrackId { get; init; } = "";
    public int? WeatherCode { get; init; }
    public IReadOnlyDictionary<string, string> Status { get; init; } = new Dictionary<string, string>();
    public SourceOption[] MusicSources { get; init; } = [];
    public CodexQuota[] Quotas { get; init; } = [];
    public DataReading Get(DataField field) => Values.TryGetValue(field, out var reading) ? reading : new("—");
}
