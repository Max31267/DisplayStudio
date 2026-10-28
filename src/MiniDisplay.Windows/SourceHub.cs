using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using MiniDisplay.Core;
using Windows.Media.Control;

namespace MiniDisplay.Windows;

public sealed class SourceHub : IAsyncDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly WeatherClient Weather = new(Http);
    private readonly CancellationTokenSource cancellation = new();
    private readonly object gate = new();
    private readonly Dictionary<DataField, DataReading> values = [];
    private readonly Dictionary<DataField, Queue<double>> history = [];
    private readonly Dictionary<string, string> status = new() { ["pc"] = "Добавьте страницу компьютера", ["weather"] = "Выберите город", ["music"] = "Добавьте музыкальную страницу", ["codex"] = "Добавьте страницу Codex" };
    private readonly Task[] workers;
    private DisplayProject project = new();
    private SourceSnapshot snapshot = new();
    private bool inspecting;
    private int refreshGeneration;
    private SourceOption[] musicSources = [];
    private CodexQuota[] quotas = [];
    private byte[]? artwork;
    private string trackId = "";
    private int? weatherCode;
    private string codexExecutable = "";
    public string CodexExecutable => Volatile.Read(ref codexExecutable);
    public void SetCodexExecutable(string path) { Volatile.Write(ref codexExecutable, path); Refresh(); }
    public SourceSnapshot Snapshot => Volatile.Read(ref snapshot);
    public void SetProject(DisplayProject value) => Volatile.Write(ref project, value);
    public void SetInspecting(bool value) => Volatile.Write(ref inspecting, value);
    public void Refresh() => Interlocked.Increment(ref refreshGeneration);
    public SourceHub()
    {
        workers = [Task.Run(ComputerLoop), Task.Run(WeatherLoop), Task.Run(MusicLoop), Task.Run(CodexLoop)];
    }
    private bool Needs(Func<PageLayer, bool> predicate) => Volatile.Read(ref inspecting) || Volatile.Read(ref project).Pages.SelectMany(page => page.Layers).Any(predicate);
    private static bool DataLayer(PageLayer layer) => layer.Type is LayerType.DataText or LayerType.Bar or LayerType.Ring or LayerType.Graph;
    private static bool PcLayer(PageLayer layer) => DataLayer(layer) && layer.DataField <= DataField.RamTotal;
    private void Publish(string source, string message, Dictionary<DataField, DataReading>? updates = null, bool samples = false)
    {
        lock (gate)
        {
            status[source] = message;
            if (updates is not null)
                foreach (var pair in updates)
                {
                    values[pair.Key] = pair.Value;
                    if (samples)
                    {
                        if (!history.TryGetValue(pair.Key, out var queue)) history[pair.Key] = queue = new();
                        queue.Enqueue(pair.Value.Number ?? double.NaN); while (queue.Count > 60) queue.Dequeue();
                    }
                }
            snapshot = new() { Values = new Dictionary<DataField, DataReading>(values), History = history.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()), Status = new Dictionary<string, string>(status), Artwork = artwork, TrackId = trackId, WeatherCode = weatherCode, MusicSources = musicSources, Quotas = quotas };
        }
    }
    private async Task ComputerLoop()
    {
        Computer? computer = null; long idleBefore = 0, totalBefore = 0;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (Needs(PcLayer))
                {
                    double? cpu = null, gpu = null, cpuTemp = null, gpuTemp = null;
                    if (GetSystemTimes(out long idle, out long kernel, out long user))
                    {
                        long total = kernel + user, difference = total - totalBefore;
                        if (totalBefore > 0 && difference > 0) cpu = Math.Clamp(100d * (difference - (idle - idleBefore)) / difference, 0, 100);
                        totalBefore = total; idleBefore = idle;
                    }
                    try
                    {
                        if (computer is null) { computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true }; computer.Open(); }
                        foreach (var hardware in computer.Hardware)
                        {
                            hardware.Update(); foreach (var child in hardware.SubHardware) child.Update();
                            if (hardware.HardwareType == HardwareType.Cpu)
                            {
                                cpuTemp = Max(hardware, SensorType.Temperature) ?? cpuTemp;
                                cpu = hardware.Sensors.FirstOrDefault(sensor => sensor.SensorType == SensorType.Load && sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))?.Value ?? cpu;
                            }
                            if (hardware.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia)
                            {
                                gpuTemp = Max(hardware, SensorType.Temperature) ?? gpuTemp;
                                var load = hardware.Sensors.Where(sensor => sensor.SensorType == SensorType.Load && (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("3D", StringComparison.OrdinalIgnoreCase))).Select(sensor => sensor.Value).Where(value => value.HasValue).ToArray();
                                if (load.Length > 0) gpu = Math.Max(gpu ?? 0, load.Max()!.Value);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { /* Unavailable sensors retain the Windows counters and missing markers. */ }
                    var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                    bool available = GlobalMemoryStatusEx(ref memory);
                    var readings = new Dictionary<DataField, DataReading>
                    {
                        [DataField.CpuLoad] = SourceParsers.Reading(cpu, "%"), [DataField.GpuLoad] = SourceParsers.Reading(gpu, "%"),
                        [DataField.CpuTemperature] = SourceParsers.Reading(cpuTemp, "°C"), [DataField.GpuTemperature] = SourceParsers.Reading(gpuTemp, "°C"),
                        [DataField.RamLoad] = SourceParsers.Reading(available ? memory.Load : null, "%"),
                        [DataField.RamUsed] = SourceParsers.Reading(available ? (memory.TotalPhysical - memory.AvailablePhysical) / 1073741824d : null, " ГиБ", "0.0"),
                        [DataField.RamTotal] = SourceParsers.Reading(available ? memory.TotalPhysical / 1073741824d : null, " ГиБ", "0.0")
                    };
                    Publish("pc", $"CPU {readings[DataField.CpuLoad].Text} · GPU {readings[DataField.GpuLoad].Text} · RAM {readings[DataField.RamLoad].Text}. Недоступные датчики: —", readings, true);
                }
                await Task.Delay(1000, cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally { computer?.Close(); }
    }
    private static double? Max(IHardware hardware, SensorType type)
    {
        var sensors = hardware.Sensors.Where(sensor => sensor.SensorType == type && sensor.Value is float value && float.IsFinite(value) && (type != SensorType.Temperature || value > 0)).ToArray();
        return sensors.Length == 0 ? null : sensors.Max(sensor => sensor.Value!.Value);
    }
    public static Task<WeatherPlace[]> SearchCity(string query, CancellationToken token) => Weather.SearchCity(query, token);
    private async Task WeatherLoop()
    {
        long next = 0; int generation = -1; string location = ""; WeatherForecast? lastGood = null;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var settings = Volatile.Read(ref project).Sources;
                string key = $"{settings.Latitude}|{settings.Longitude}|{settings.WeatherCity}";
                if (key != location)
                {
                    location = key; lastGood = null; next = 0;
                    PublishWeather(settings.Latitude.HasValue ? "Получаем погоду…" : "Укажите город, чтобы получать погоду", WeatherEmpty(settings.WeatherCity), null);
                }
                if (generation != refreshGeneration) { next = 0; generation = refreshGeneration; }
                bool SameLocation() { var current = Volatile.Read(ref project).Sources; return current.WeatherCity == settings.WeatherCity && current.Latitude == settings.Latitude && current.Longitude == settings.Longitude; }
                if (Needs(layer => layer.Type == LayerType.WeatherIcon || DataLayer(layer) && (layer.DataField is >= DataField.WeatherCity and <= DataField.WeatherSummary || layer.DataField == DataField.WeatherSource)) && Environment.TickCount64 >= next)
                {
                    next = Environment.TickCount64 + 600000;
                    if (settings.Latitude is null) PublishWeather("Укажите город, чтобы получать погоду", WeatherEmpty(settings.WeatherCity), null);
                    else
                    {
                        try
                        {
                            Publish("weather", "Получаем погоду…");
                            var forecast = await Weather.Fetch(settings.Latitude.Value, settings.Longitude!.Value, DateTimeOffset.UtcNow, cancellation.Token);
                            if (!SameLocation()) continue;
                            lastGood = forecast;
                            PublishWeather(settings.WeatherCity + " · " + forecast.Provider + " · на " + forecast.ValidAt.ToLocalTime().ToString("HH:mm") + " · обновлено " + DateTime.Now.ToShortTimeString(), forecast.Readings(settings.WeatherCity), forecast.Code);
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellation.IsCancellationRequested)
                        {
                            if (!SameLocation()) continue;
                            next = Environment.TickCount64 + 30000;
                            if (lastGood is not null && Math.Abs((DateTimeOffset.UtcNow - lastGood.ValidAt).TotalHours) <= 2)
                                PublishWeather("Нет связи · " + lastGood.Provider + " · данные на " + lastGood.ValidAt.ToLocalTime().ToString("HH:mm") + ". Повтор через 30 с.", lastGood.Readings(settings.WeatherCity), lastGood.Code);
                            else PublishWeather("Не удалось получить погоду: Open-Meteo и MET Norway недоступны. Повтор через 30 с.", WeatherEmpty(settings.WeatherCity), null);
                        }
                    }
                }
                await Task.Delay(1000, cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    private void PublishWeather(string message, Dictionary<DataField, DataReading> readings, int? code)
    { lock (gate) { weatherCode = code; Publish("weather", message, readings); } }
    private static Dictionary<DataField, DataReading> WeatherEmpty(string city) => new()
    {
        [DataField.WeatherCity] = new(string.IsNullOrWhiteSpace(city) ? "Выберите город" : city.Split(',')[0]), [DataField.WeatherTemperature] = new("—"), [DataField.WeatherFeelsLike] = new("—"), [DataField.WeatherHumidity] = new("—"), [DataField.WeatherSummary] = new("Нет данных"), [DataField.WeatherSource] = new("—")
    };
    private async Task MusicLoop()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager = null;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (Needs(layer => layer.Type == LayerType.Artwork || DataLayer(layer) && layer.DataField is >= DataField.MusicTitle and <= DataField.MusicProgress))
                {
                    try
                    {
                        manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellation.Token);
                        var sessions = manager.GetSessions(); string selected = Volatile.Read(ref project).Sources.MusicSourceId;
                        var session = selected.Length == 0 ? manager.GetCurrentSession() : sessions.FirstOrDefault(item => item.SourceAppUserModelId == selected);
                        lock (gate) musicSources = sessions.Select(item => new SourceOption(item.SourceAppUserModelId, item.SourceAppUserModelId)).DistinctBy(item => item.Id).ToArray();
                        if (session is null) { ClearMusic("Нет активной медиасессии"); }
                        else
                        {
                            var properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellation.Token);
                            var timeline = session.GetTimelineProperties(); var playback = session.GetPlaybackInfo();
                            double duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);
                            double position = Math.Max(0, (timeline.Position - timeline.StartTime).TotalSeconds);
                            bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                            if (playing) position += Math.Max(0, (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds) * (playback.PlaybackRate ?? 1);
                            if (duration > 0) position = Math.Min(position, duration);
                            string id = session.SourceAppUserModelId + "|" + properties.Title + "|" + properties.Artist;
                            if (id != trackId)
                            {
                                byte[]? cover = null;
                                if (properties.Thumbnail is not null)
                                {
                                    using var stream = await properties.Thumbnail.OpenReadAsync().AsTask(cancellation.Token);
                                    if (stream.Size <= 4 * 1024 * 1024) { using var input = stream.AsStreamForRead(); using var output = new MemoryStream(); await input.CopyToAsync(output, cancellation.Token); cover = output.ToArray(); }
                                }
                                lock (gate) { artwork = cover; trackId = id; }
                            }
                            Publish("music", session.SourceAppUserModelId + (playing ? " · воспроизводится" : " · пауза"), new()
                            {
                                [DataField.MusicTitle] = new(string.IsNullOrWhiteSpace(properties.Title) ? "Без названия" : properties.Title), [DataField.MusicArtist] = new(properties.Artist ?? "—"), [DataField.MusicAlbum] = new(properties.AlbumTitle ?? "—"),
                                [DataField.MusicState] = new(playing ? "Воспроизводится" : "Пауза"), [DataField.MusicProgress] = SourceParsers.Reading(duration > 0 ? 100 * position / duration : null, "%"),
                                [DataField.MusicPosition] = new(Time(position), position), [DataField.MusicDuration] = duration > 0 ? new(Time(duration), duration) : new("—")
                            });
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException && !cancellation.IsCancellationRequested) { manager = null; ClearMusic("Медиасессия недоступна: " + ex.Message); }
                }
                await Task.Delay(1000, cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    private static string Time(double seconds) => seconds >= 3600 ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(seconds).ToString(@"m\:ss");
    private void ClearMusic(string message)
    {
        lock (gate) { artwork = null; trackId = ""; }
        Publish("music", message, Enum.GetValues<DataField>().Where(field => field is >= DataField.MusicTitle and <= DataField.MusicProgress).ToDictionary(field => field, _ => new DataReading("—")));
    }
    private async Task CodexLoop()
    {
        long next = 0; int generation = -1; string settingKey = "";
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var settings = Volatile.Read(ref project).Sources;
                string configured = CodexExecutable;
                string key = configured + "|" + settings.CodexLimitId;
                if (generation != refreshGeneration || key != settingKey) { generation = refreshGeneration; settingKey = key; next = 0; }
                if (Needs(layer => DataLayer(layer) && layer.DataField is >= DataField.CodexPrimaryRemaining and <= DataField.CodexSecondaryWindow) && Environment.TickCount64 >= next)
                {
                    next = Environment.TickCount64 + 60000;
                    try
                    {
                        var result = await CodexReader.Read(configured, cancellation.Token);
                        lock (gate) quotas = result;
                        var quota = result.FirstOrDefault(item => item.Id == settings.CodexLimitId) ?? result.FirstOrDefault();
                        if (quota is null) throw new InvalidDataException("Лимиты недоступны для текущего входа Codex.");
                        Publish("codex", quota.Name + " · обновлено " + DateTime.Now.ToShortTimeString(), QuotaReadings(quota));
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException && !cancellation.IsCancellationRequested)
                    {
                        lock (gate) quotas = [];
                        Publish("codex", "Codex недоступен: " + ex.Message, QuotaReadings(null));
                    }
                }
                await Task.Delay(1000, cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    private static Dictionary<DataField, DataReading> QuotaReadings(CodexQuota? quota) => new()
    {
        [DataField.CodexPrimaryRemaining] = SourceParsers.Reading(quota?.Primary?.Remaining, "%"), [DataField.CodexSecondaryRemaining] = SourceParsers.Reading(quota?.Secondary?.Remaining, "%"),
        [DataField.CodexPrimaryWindow] = new(SourceParsers.WindowName(quota?.Primary?.Minutes)), [DataField.CodexSecondaryWindow] = new(SourceParsers.WindowName(quota?.Secondary?.Minutes)),
        [DataField.CodexPrimaryReset] = new(SourceParsers.ResetName(quota?.Primary?.ResetAt, DateTimeOffset.UtcNow)), [DataField.CodexSecondaryReset] = new(SourceParsers.ResetName(quota?.Secondary?.ResetAt, DateTimeOffset.UtcNow))
    };
    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel(); var stopped = Task.WhenAll(workers);
        try { await stopped.WaitAsync(TimeSpan.FromSeconds(4)); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        if (stopped.IsCompleted) cancellation.Dispose();
        else _ = stopped.ContinueWith(task => { _ = task.Exception; cancellation.Dispose(); }, TaskScheduler.Default);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtended; }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}
