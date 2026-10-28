using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using MiniDisplay.Core;

if (args is ["--verify-bundle", var bundleRoot])
{
    var bundle = FirmwareBundle.Load(bundleRoot);
    foreach (var profile in bundle.Profiles)
    {
        var command = bundle.Prepare(bundleRoot, profile.Id, "COM42");
        if (command.Arguments[^2] != "0x0" || !File.Exists(command.Executable))
            throw new InvalidDataException("Invalid release flash command");
        Console.WriteLine($"VERIFIED {profile.Id}: integrity and command, no device accessed");
    }
    return 0;
}

var tests = new List<(string Name, Func<Task> Run)>
{
    ("CRC32 IEEE known vector", () => Sync(() => Equal(0xcbf43926u, Wire.Crc32("123456789"u8)))),
    ("Serial fragmentation and concatenation", () => Sync(Fragmentation)),
    ("CRC corruption, boot noise and oversize recovery", () => Sync(Corruption)),
    ("Tile merging, limits and acknowledged baseline", () => Sync(Differences)),
    ("Full frame, delta and unchanged frame round trip", RoundTrip),
    ("Brightness ACK, range, reconnect and legacy capability fallback", BacklightControl),
    ("Invalid brightness commands never change level or pixels", () => Sync(InvalidBrightness)),
    ("Damaged frame requests resynchronization", Resynchronization),
    ("Lost acknowledgement never commits baseline", LostAck),
    ("Reject out-of-order, out-of-bounds and incomplete frames", () => Sync(InvalidFrames)),
    ("Project round trip and validation", () => Sync(ProjectStorage)),
    ("Page durations, ordering, pauses and disabled pages", () => Sync(PageTiming)),
    ("Legacy projects migrate and duplicated pages are independent", () => Sync(ProjectMigration)),
    ("Undo, redo and grouped edits preserve complete projects", () => Sync(EditHistory)),
    ("Invalid edits never change history; branching drops redo", () => Sync(HistoryValidation)),
    ("Malformed and oversized projects are rejected safely", () => Sync(InvalidProjects)),
    ("Portable resources, JSON compatibility and package validation", () => Sync(AssetPackages)),
    ("Source settings, quota groups and missing values", () => Sync(SourceValues)),
    ("Image resize anchors, aspect ratio, bounds and grouped undo", () => Sync(ImageResize)),
    ("Firmware bundle integrity, chip and serial arguments", () => Sync(FirmwareFiles)),
    ("Weather TLS failure switches to current MET forecast", WeatherFallback),
    ("Weather primary, missing fields and invalid response fallback", WeatherPrimary),
    ("Weather rejects stale/oversized data and preserves cancellation", WeatherFailures),
    ("New-track interruption respects pinning and returns to schedule", () => Sync(MusicInterruption)),
    ("250 frames with mutations and repeated reconnects", Stress)
};
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MD_NATIVE_BRIDGE")))
    tests.Add(("Host exchanges frames with compiled firmware C receiver", NativeRoundTrip));
else Console.WriteLine("SKIP  Native firmware bridge (MD_NATIVE_BRIDGE is not set)");
if (args.Length == 1 && args[0] == "--weather-live")
{
    using var http = new HttpClient();
    var forecast = await new WeatherClient(http).Fetch(55.75, 37.62, DateTimeOffset.UtcNow, default);
    Console.WriteLine($"LIVE WEATHER  {forecast.Provider} · {forecast.ValidAt:O} · {forecast.Temperature}°C · humidity {forecast.Humidity}% · {forecast.Summary}");
}
int failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS  " + test.Name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL  " + test.Name + "\n" + ex); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed");
return failed == 0 ? 0 : 1;

static Task Sync(Action action) { action(); return Task.CompletedTask; }
static void FirmwareFiles()
{
    string root=Path.Combine(Path.GetTempPath(),"DisplayStudio-bundle-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(root,"Firmware")); Directory.CreateDirectory(Path.Combine(root,"Tools"));
    try
    {
        byte[] binary=new byte[32]; binary[0]=0xe9; byte[] tool=[1,2,3,4];
        string Hash(byte[] data)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
        File.WriteAllBytes(Path.Combine(root,"Firmware","test.bin"),binary); File.WriteAllBytes(Path.Combine(root,"Tools","esptool.exe"),tool);
        var bundle=new FirmwareBundle { Version="test",Tool="Tools/esptool.exe",ToolSha256=Hash(tool),Profiles=[new("default","Default","Firmware/test.bin",Hash(binary))] };
        string manifest=Path.Combine(root,"Firmware","manifest.json");
        void Save(FirmwareBundle value)=>File.WriteAllText(manifest,JsonSerializer.Serialize(value));
        Save(bundle); var loaded=FirmwareBundle.Load(root+Path.DirectorySeparatorChar); var command=loaded.Prepare(root+Path.DirectorySeparatorChar,"default","COM12");
        Check(command.Arguments.SequenceEqual(new[]{"--chip","esp32c3","--port","COM12","--baud","460800","write-flash","0x0",Path.Combine(root,"Firmware","test.bin")}),"Flashing must use only the selected C3 port and a merged image at zero");
        foreach(string port in new[]{"COM0","COM99999","COM3 --erase-all","../COM1",""})
        { try{loaded.Prepare(root,"default",port);throw new Exception("Invalid port accepted");}catch(ArgumentException){} }
        File.WriteAllBytes(Path.Combine(root,"Firmware","test.bin"),[0xe9,1,2]);
        try{loaded.Prepare(root,"default","COM1");throw new Exception("Corrupt binary accepted");}catch(InvalidDataException){}
        foreach(var bad in new[]{bundle with {Chip="esp32"},bundle with {Tool="../outside.exe"},bundle with {Tool=Path.Combine(root,"Tools","esptool.exe")},bundle with {Profiles=[bundle.Profiles[0],bundle.Profiles[0]]}})
        { Save(bad); try{FirmwareBundle.Load(root);throw new Exception("Unsafe bundle accepted");}catch(InvalidDataException){} }
    }
    finally{if(Path.GetDirectoryName(Path.GetFullPath(root))==Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())))Directory.Delete(root,true);}
}
static void ImageResize()
{
    var original = new PageLayer { Type=LayerType.Image, X=30,Y=40,Width=80,Height=40 };
    Equal(8,LayerResize.Anchors(original).Length); Check(LayerResize.HitTest(original,110,80,3)==ResizeHandle.BottomRight);
    Check(LayerResize.HitTest(original,70,60,3)==ResizeHandle.None); Equal(0,LayerResize.Anchors(new PageLayer()).Length);
    var grown = LayerResize.Apply(original,ResizeHandle.BottomRight,40,20,true);
    Equal(120,grown.Width); Equal(60,grown.Height); Equal(30,grown.X); Equal(40,grown.Y);
    var topLeft = LayerResize.Apply(original,ResizeHandle.TopLeft,-20,-10,true);
    Equal(10,topLeft.X); Equal(30,topLeft.Y); Equal(100,topLeft.Width); Equal(50,topLeft.Height);
    var side = LayerResize.Apply(original,ResizeHandle.Right,30,99,true);
    Equal(110,side.Width); Equal(original.Height,side.Height); Equal(original.Y,side.Y);
    var crossed = LayerResize.Apply(original,ResizeHandle.Left,1000,0,false); Equal(1,crossed.Width); Equal(110,crossed.X+crossed.Width);
    foreach (var layer in new[] { original, original with { X=230,Y=220,Width=100,Height=70 }, original with { X=0,Y=0,Width=240,Height=240 }, original with { X=239,Y=239,Width=1,Height=1 } })
    foreach (var handle in Enum.GetValues<ResizeHandle>().Where(handle=>handle!=ResizeHandle.None))
    foreach (bool proportional in new[] {false,true})
    foreach (var delta in new[] {(X:-1000d,Y:-700d),(X:1000d,Y:700d),(X:13d,Y:-5d)})
    {
        var result = LayerResize.Apply(layer,handle,delta.X,delta.Y,proportional); result.Validate();
        Check(result.Width>=1 && result.Height>=1 && result.X+result.Width<=240 && result.Y+result.Height<=240,"Resize must stay inside the display without flipping");
    }
    var asset = ProjectAsset.Create("image.png",File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixtures","alpha.png")));
    original = original with { AssetId=asset.Id };
    var page = new DisplayPage { Layers=[original] }; var project = new DisplayProject { Assets=[asset],Pages=[page] }; var history = new ProjectHistory(project);
    for(int i=1;i<=20;i++) { var resized=LayerResize.Apply(original,ResizeHandle.BottomRight,i*2,i,true); history.Apply(project with { Pages=[page with { Layers=[resized] }] },"resize:"+original.Id); }
    var final = history.Current; Check(ProjectHistory.Equivalent(project,history.Undo())); Check(!history.CanUndo); Check(ProjectHistory.Equivalent(final,history.Redo()));
}
static HttpResponseMessage WeatherJson(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
static DateTimeOffset WeatherTime() => new(2026, 10, 28, 13, 5, 0, TimeSpan.Zero);
static string MetForecast(string symbol = "clearsky_day", bool stale = false) => JsonSerializer.Serialize(new
{
    properties = new { timeseries = new[]
    {
        new { time = "2026-10-28T10:00:00Z", data = new { instant = new { details = new { air_temperature = 30d, relative_humidity = 20d, wind_speed = 0d } }, next_1_hours = new { summary = new { symbol_code = "cloudy" } } } },
        new { time = stale ? "2026-10-27T13:00:00Z" : "2026-10-28T13:00:00Z", data = new { instant = new { details = new { air_temperature = 9d, relative_humidity = 58.9, wind_speed = 1.9 } }, next_1_hours = new { summary = new { symbol_code = symbol } } } }
    } }
});
static async Task WeatherFallback()
{
    var hosts = new List<string>();
    using var http = new HttpClient(new WeatherHandler((request, _) =>
    {
        hosts.Add(request.RequestUri!.Host);
        Check(request.RequestUri.Query.Contains("55.1235") && request.RequestUri.Query.Contains("37.9877"), "Coordinates must be invariant and rounded to four decimals");
        Check(request.Headers.UserAgent.ToString().StartsWith("DisplayStudio/"), "Weather requests must identify this app");
        return request.RequestUri.Host == "api.open-meteo.com" ? throw new HttpRequestException("TLS EOF") : Task.FromResult(WeatherJson(MetForecast()));
    }));
    var forecast = await new WeatherClient(http).Fetch(55.123456, 37.987654, WeatherTime(), default);
    Equal("api.open-meteo.com,api.met.no", string.Join(',',hosts)); Equal("MET Norway", forecast.Provider);
    Equal(9d, forecast.Temperature); Equal(58.9, forecast.Humidity!.Value); Equal(0, forecast.Code!.Value); Equal("Ясно", forecast.Summary);
    Check(forecast.FeelsLike is > 5.8 and < 6.0 && forecast.EstimatedFeelsLike);
    var readings = forecast.Readings("Москва, Россия"); Equal("Москва", readings[DataField.WeatherCity].Text);
    Check(readings[DataField.WeatherFeelsLike].Text.StartsWith('≈')); Equal("MET Norway", readings[DataField.WeatherSource].Text);
}
static async Task WeatherPrimary()
{
    int calls = 0;
    string primary = JsonSerializer.Serialize(new { current = new { time = WeatherTime().ToUnixTimeSeconds(), temperature_2m = -3.5 } });
    using var http = new HttpClient(new WeatherHandler((_, _) => { calls++; return Task.FromResult(WeatherJson(primary)); }));
    var forecast = await new WeatherClient(http).Fetch(-33.1, -70.2, WeatherTime(), default);
    Equal(1,calls); Equal("Open-Meteo",forecast.Provider); Equal(-3.5,forecast.Temperature); Check(forecast.Humidity is null && forecast.FeelsLike is null && forecast.Code is null);
    Equal("—",forecast.Readings("Тест")[DataField.WeatherFeelsLike].Text);
    foreach (string invalid in new[] { "{", "{}", "{\"current\":{\"temperature_2m\":999,\"time\":0}}" })
    {
        using var fallback = new HttpClient(new WeatherHandler((request, _) => Task.FromResult(WeatherJson(request.RequestUri!.Host == "api.met.no" ? MetForecast("lightsleetshowers_night") : invalid))));
        var result = await new WeatherClient(fallback).Fetch(55,37,WeatherTime(),default); Equal("MET Norway", result.Provider); Equal("Дождь со снегом",result.Summary);
    }
}
static async Task WeatherFailures()
{
    using var stale = new HttpClient(new WeatherHandler((_, _) => Task.FromResult(WeatherJson(MetForecast(stale:true)))));
    try { await new WeatherClient(stale).Fetch(55,37,WeatherTime(),default); throw new Exception("Stale forecast accepted"); } catch (IOException) { }
    int calls = 0;
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    using var http = new HttpClient(new WeatherHandler((_, _) => { calls++; return Task.FromResult(WeatherJson("{}")); }));
    try { await new WeatherClient(http).Fetch(55,37,WeatherTime(),cancelled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
    Equal(0,calls);
    using var tooLarge = new HttpClient(new WeatherHandler((_, _) => Task.FromResult(WeatherJson(new string('x',1024*1024+1)))));
    try { await new WeatherClient(tooLarge).Fetch(55,37,WeatherTime(),default); throw new Exception("Oversized weather response accepted"); } catch (IOException) { }
    using var unavailable = new HttpClient(new WeatherHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
    try { await new WeatherClient(unavailable).Fetch(55,37,WeatherTime(),default); throw new Exception("HTTP error accepted"); } catch (IOException) { }
}
static void AssetPackages()
{
    var asset = ProjectAsset.Create("sample.png", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixtures","alpha.png")));
    var initial = new DisplayProject();
    var page = new DisplayPage { Layers = [new() { Type = LayerType.Image, AssetId = asset.Id, Width = 100, Height = 50 }] };
    var project = initial with { Assets = [asset], Pages = [page, page.Duplicate()], Sources = new() { WeatherCity = "Тест", Latitude = 55, Longitude = 37 } };
    string path = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".mdisplay"), jsonPath = path + ".json";
    try
    {
        project.Save(path); var loaded = DisplayProject.Load(path);
        Check(ProjectHistory.Equivalent(project, loaded)); Check(asset.Data.AsSpan().SequenceEqual(loaded.Assets[0].Data));
        using (var archive = System.IO.Compression.ZipFile.OpenRead(path)) Equal(2, archive.Entries.Count);
        project.Save(jsonPath); Check(ProjectHistory.Equivalent(project, DisplayProject.Load(jsonPath)));
        var foreign = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(jsonPath))!;
        foreign["Sources"]!["CodexExecutable"] = "C:/Windows/System32/cmd.exe";
        File.WriteAllText(jsonPath,foreign.ToJsonString()); var sanitized = DisplayProject.Load(jsonPath); sanitized.Save(jsonPath);
        Check(!File.ReadAllText(jsonPath).Contains("CodexExecutable",StringComparison.Ordinal),"Imported project must not configure an executable");
        var history = new ProjectHistory(initial); history.Apply(project); Check(ProjectHistory.Equivalent(initial, history.Undo())); Check(ProjectHistory.Equivalent(project, history.Redo()));
        foreach (var invalid in new[] { project with { Assets = [] }, project with { Assets = [asset with { Data = [1,2,3] }] }, project with { Assets = [asset, asset] }, project with { Sources = new() { Latitude = 10 } } })
        { try { invalid.Validate(); throw new Exception("Invalid resource/settings accepted"); } catch (InvalidDataException) { } }
        using (var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update)) archive.CreateEntry("../outside.txt");
        try { DisplayProject.Load(path); throw new Exception("Unexpected package entry accepted"); } catch (InvalidDataException) { }
    }
    finally { File.Delete(path); File.Delete(jsonPath); }
}
static void SourceValues()
{
    using var json = System.Text.Json.JsonDocument.Parse("""
    {"result":{"rateLimitsByLimitId":{"codex":{"limitName":"Основная","primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":2000000000},"secondary":null},"other":{"primary":{"usedPercent":null,"windowDurationMins":15},"secondary":{"usedPercent":130,"windowDurationMins":10080}}}}}
    """);
    var groups = SourceParsers.Codex(json.RootElement); Equal(2, groups.Length);
    Equal(75d, groups[0].Primary!.Remaining!.Value); Check(groups[0].Secondary is null); Check(groups[1].Primary!.Remaining is null); Equal(0d, groups[1].Secondary!.Remaining!.Value);
    Equal("5 ч", SourceParsers.WindowName(groups[0].Primary!.Minutes)); Equal("7 д", SourceParsers.WindowName(groups[1].Secondary!.Minutes)); Equal("15 мин", SourceParsers.WindowName(15));
    Equal("—", new SourceSnapshot().Get(DataField.CpuLoad).Text); Check(new SourceSnapshot().Get(DataField.CpuLoad).Number is null);
    foreach (var template in new[] { WidgetTemplates.Weather(), WidgetTemplates.Computer(), WidgetTemplates.Music(), WidgetTemplates.Codex() }) new DisplayProject { Pages = [template] }.Validate();
    using var legacy = System.Text.Json.JsonDocument.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":-10},\"secondary\":null}}");
    Equal(100d, SourceParsers.Codex(legacy.RootElement)[0].Primary!.Remaining!.Value);
    using var missing = System.Text.Json.JsonDocument.Parse("{\"rateLimitsByLimitId\":{\"bad\":null}}"); Equal(0, SourceParsers.Codex(missing.RootElement).Length);
}
static void MusicInterruption()
{
    var project = new DisplayProject { Pages = [PageTemplates.Clock(), WidgetTemplates.Music()] }; var playback = new PagePlayback();
    playback.Select(project.Pages[0].Id, TimeSpan.Zero); Check(playback.Interrupt(project, project.Pages[1].Id, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)));
    Equal(project.Pages[1].Id, playback.Update(project, TimeSpan.FromSeconds(14)).Id); Equal(1, playback.RemainingSeconds(project.Pages[1], TimeSpan.FromSeconds(14)));
    Equal(project.Pages[0].Id, playback.Update(project, TimeSpan.FromSeconds(15)).Id);
    Check(!playback.Interrupt(project with { AutoRotate = false }, project.Pages[1].Id, TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(10)));
    playback.Interrupt(project, project.Pages[1].Id, TimeSpan.FromSeconds(17), TimeSpan.FromSeconds(10)); playback.Select(project.Pages[0].Id, TimeSpan.FromSeconds(18));
    Equal(project.Pages[0].Id, playback.Update(project, TimeSpan.FromSeconds(19)).Id);
}
static void EditHistory()
{
    var initial = new DisplayProject(); var history = new ProjectHistory(initial);
    DisplayProject Rename(string name) => initial with { Pages = [initial.Pages[0] with { Name = name }, initial.Pages[1]] };
    Check(!history.Apply(initial with { Pages = initial.Pages.ToArray() })); Check(!history.CanUndo);
    history.Apply(Rename("Ч"), "name"); history.Apply(Rename("Часы ПК"), "name");
    Check(ProjectHistory.Equivalent(initial, history.Undo())); Check(!history.CanUndo);
    Equal("Часы ПК", history.Redo().Pages[0].Name);
    history.BreakGroup();
    var copied = history.Current.Pages[0].Duplicate();
    history.Apply(history.Current with { Pages = [.. history.Current.Pages, copied] });
    Equal(3, history.Current.Pages.Length); Equal(2, history.Undo().Pages.Length);
    Equal(copied.Id, history.Redo().Pages[2].Id);
    var page = history.Current.Pages[0]; var layer = page.Layers[0];
    for (int i = 1; i <= 20; i++)
    {
        page = page with { Layers = [layer with { X = i }, .. page.Layers.Skip(1)] };
        history.Apply(history.Current with { Pages = [page, .. history.Current.Pages.Skip(1)] }, "drag");
    }
    Equal(20, history.Current.Pages[0].Layers[0].X);
    Equal(layer.X, history.Undo().Pages[0].Layers[0].X);
    Equal(20, history.Redo().Pages[0].Layers[0].X);
}
static void HistoryValidation()
{
    var initial = new DisplayProject(); var history = new ProjectHistory(initial);
    try { history.Apply(initial with { Pages = [] }); throw new Exception("Invalid history entry accepted"); }
    catch (InvalidDataException) { }
    Check(ProjectHistory.Equivalent(initial, history.Current)); Check(!history.CanUndo);
    history.Apply(initial with { AutoRotate = false }); history.Undo(); Check(history.CanRedo);
    history.Apply(initial with { FramesPerSecond = 10 }); Check(!history.CanRedo);
    for (int i = 0; i < 120; i++) history.Apply(history.Current with { AutoRotate = !history.Current.AutoRotate });
    int count = 0; while (history.CanUndo) { history.Undo(); count++; }
    Equal(100, count);
    history.Reset(initial); Check(!history.CanRedo && !history.CanUndo);
}
static void InvalidProjects()
{
    string path = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".json");
    try
    {
        foreach (string json in new[] { "[]", "null", "{\"Version\":\"2\"}", "{\"Version\":2,\"Pages\":null}", "{\"Version\":2,\"Pages\":[]}", "{\"Version\":9000000000000}" })
        {
            File.WriteAllText(path, json);
            try { DisplayProject.Load(path); throw new Exception("Invalid project accepted: " + json); }
            catch (InvalidDataException) { }
        }
        File.WriteAllText(path, "{broken");
        try { DisplayProject.Load(path); throw new Exception("Malformed JSON accepted"); }
        catch (System.Text.Json.JsonException) { }
        using (var stream = File.Create(path)) stream.SetLength(8 * 1024 * 1024 + 1);
        try { DisplayProject.Load(path); throw new Exception("Oversized project accepted"); }
        catch (InvalidDataException) { }
    }
    finally { File.Delete(path); }
}
static void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
static void Equal<T>(T expected, T actual) where T : IEquatable<T> => Check(expected.Equals(actual), $"Expected {expected}; actual {actual}");
static void Same(byte[] expected, byte[] actual) => Check(expected.AsSpan().SequenceEqual(actual), "Pixels differ");
static void Fragmentation()
{
    var payload = Enumerable.Range(0, 7688).Select(i => (byte)i).ToArray();
    var first = Wire.Encode(new Packet(MessageType.Pixels, 0x12345678, payload));
    var second = Wire.Encode(new Packet(MessageType.Ping, uint.MaxValue, []));
    for (int chunk = 1; chunk <= 257; chunk += 16)
    {
        var decoder = new PacketDecoder();
        var packets = new List<Packet>();
        byte[] data = [.. first, .. second];
        for (int offset = 0; offset < data.Length; offset += chunk)
            packets.AddRange(decoder.Feed(data.AsSpan(offset, Math.Min(chunk, data.Length - offset))));
        Equal(2, packets.Count); Equal(0x12345678u, packets[0].Sequence);
        Equal(uint.MaxValue, packets[1].Sequence); Same(payload, packets[0].Payload);
    }
}
static void Corruption()
{
    var good = Wire.Encode(new Packet(MessageType.Ping, 9, []));
    var bad = Wire.Encode(new Packet(MessageType.Pixels, 8, new byte[128])); bad[^1] ^= 1;
    var oversize = (byte[])good.Clone(); oversize[6] = 0xff; oversize[7] = 0xff;
    var decoder = new PacketDecoder();
    var packets = decoder.Feed([.. "ESP-ROM boot log\r\n"u8, .. bad, .. oversize, .. good]);
    Equal(1, packets.Count); Equal(9u, packets[0].Sequence); Check(decoder.RejectedPackets >= 2);
    var random = new Random(42); var noise = new byte[50000]; random.NextBytes(noise);
    decoder.Feed(noise); // Storage remains bounded for arbitrary input.
}
static void Differences()
{
    var differ = new FrameDiffer(); var frame = new byte[FrameDiffer.FrameBytes];
    var blocks = differ.Compare(frame); Equal(15, blocks.Count); Equal((ushort)240, blocks[0].Width);
    Equal(225, differ.Compare(frame, 520).Count);
    differ.Acknowledge(frame); Equal(0, differ.Compare(frame).Count);
    frame[(19 * 240 + 21) * 2] = 0xff;
    blocks = differ.Compare(frame); Equal(1, blocks.Count); Equal((ushort)16, blocks[0].X); Equal((ushort)16, blocks[0].Y);
    // An unacknowledged frame does not alter the comparison baseline.
    Equal(1, differ.Compare(frame).Count);
    differ.Invalidate(); Equal(15, differ.Compare(frame).Count);
}
static async Task RoundTrip()
{
    var link = new EmulatorLink(); await using var session = new DisplaySession(link);
    await session.ConnectAsync(default);
    var random = new Random(1); var frame = new byte[FrameDiffer.FrameBytes]; random.NextBytes(frame);
    Check(await session.SendFrameAsync(frame, default)); Same(frame, link.Device.Pixels);
    Equal(1, link.Device.CommittedFrames); Check(!await session.SendFrameAsync(frame, default));
    long before = session.BytesSent; frame[1000] ^= 0xff;
    await session.SendFrameAsync(frame, default); Same(frame, link.Device.Pixels);
    Check(session.BytesSent - before < 1024, "Single tile should use less than 1 KiB");
}
static async Task BacklightControl()
{
    var link = new EmulatorLink(); await using var session = new DisplaySession(link);
    await session.ConnectAsync(default); Check(session.Capabilities!.SupportsBrightness);
    var frame = new byte[FrameDiffer.FrameBytes]; new Random(65).NextBytes(frame);
    await session.SendFrameAsync(frame, default);
    foreach (int percent in new[] { 0, 1, 37, 100, 80 })
    {
        Check(await session.SetBrightnessAsync(percent, default)); Equal(percent, link.Device.BrightnessPercent);
        Same(frame, link.Device.Pixels); Check(!await session.SendFrameAsync(frame, default));
    }
    long before = session.BytesSent;
    foreach (int percent in new[] { -1, 101, int.MaxValue })
    {
        try { await session.SetBrightnessAsync(percent, default); throw new Exception("Bad brightness accepted"); }
        catch (ArgumentOutOfRangeException) { }
    }
    Equal(before, session.BytesSent);
    await session.ConnectAsync(default); Equal(80, link.Device.BrightnessPercent);
    await session.SetBrightnessAsync(42, default); Equal(42, link.Device.BrightnessPercent);
    await session.SendFrameAsync(frame, default); Same(frame, link.Device.Pixels);
    foreach (var device in new[] { new DeviceEmulator { SupportsExtendedHello = false }, new DeviceEmulator { SupportsBacklight = false } })
    {
        await using var legacy = new DisplaySession(new EmulatorLink { Device = device });
        await legacy.ConnectAsync(default); Check(!legacy.Capabilities!.SupportsBrightness);
        long bytes = legacy.BytesSent;
        Check(!await legacy.SetBrightnessAsync(5, default)); Equal(bytes, legacy.BytesSent);
        await legacy.SendFrameAsync(frame, default); Same(frame, device.Pixels);
    }
    await using var offline = new DisplaySession(new EmulatorLink());
    try { await offline.SetBrightnessAsync(50, default); throw new Exception("Offline brightness accepted"); }
    catch (InvalidOperationException) { }
}

static void InvalidBrightness()
{
    var device = new DeviceEmulator(); var decoder = new PacketDecoder();
    Packet Send(MessageType type, uint seq, byte[] body) => decoder.Feed(device.Receive(Wire.Encode(new Packet(type, seq, body)))).Single();
    var caps = Send(MessageType.Hello, 0, [1]);
    Check(DeviceCapabilities.Parse(caps.Payload).SupportsBrightness);
    Check(Send(MessageType.SetBrightness, 1, [0]).Type == MessageType.Ack);
    foreach (var body in new byte[][] { [], [101], [255], [50, 0] })
    {
        Send(MessageType.Hello, 100, [1]);
        Check(Send(MessageType.SetBrightness, 101, body).Type == MessageType.RequestFull);
        Equal(0, device.BrightnessPercent);
    }
    Send(MessageType.Hello, 200, [1]);
    Check(Send(MessageType.SetBrightness, 202, [80]).Type == MessageType.RequestFull); Equal(0, device.BrightnessPercent);
    Send(MessageType.Hello, 300, [1]); Send(MessageType.FrameBegin, 301, [1, 0, 0, 0, 1]);
    Check(Send(MessageType.SetBrightness, 302, [80]).Type == MessageType.RequestFull); Equal(0, device.BrightnessPercent);
    Send(MessageType.Hello, 400, [1]);
    var corrupt = Wire.Encode(new Packet(MessageType.SetBrightness, 401, [80])); corrupt[^1] ^= 1;
    device.Receive(corrupt); Equal(0, device.BrightnessPercent); Check(device.Pixels.All(value => value == 0));
    Check(!DeviceCapabilities.Parse(Send(MessageType.Hello, 500, []).Payload).SupportsBrightness);
}

static async Task Resynchronization()
{
    var link = new EmulatorLink(); await using var session = new DisplaySession(link);
    await session.ConnectAsync(default);
    var frame = new byte[FrameDiffer.FrameBytes]; await session.SendFrameAsync(frame, default);
    frame[0] = 255; link.CorruptNextWrite = true;
    try { await session.SendFrameAsync(frame, default); throw new Exception("Corruption accepted"); }
    catch (FullFrameRequiredException) { }
    Equal(1, link.Device.CommittedFrames);
    await session.ConnectAsync(default); long before = session.BytesSent;
    await session.SendFrameAsync(frame, default);
    Check(session.BytesSent - before > FrameDiffer.FrameBytes); Same(frame, link.Device.Pixels);
}
static async Task LostAck()
{
    var link = new DroppingLink(); await using var session = new DisplaySession(link) { ResponseTimeout = TimeSpan.FromMilliseconds(40) };
    await session.ConnectAsync(default);
    var frame = new byte[FrameDiffer.FrameBytes];
    link.DropFrameEnd = true;
    try { await session.SendFrameAsync(frame, default); throw new Exception("Missing ACK accepted"); }
    catch (TimeoutException) { }
    Equal(0, session.FramesSent);
    await session.ConnectAsync(default); long before = session.BytesSent;
    await session.SendFrameAsync(frame, default);
    Check(session.BytesSent - before > FrameDiffer.FrameBytes); Equal(1, session.FramesSent);
}
static void InvalidFrames()
{
    var device = new DeviceEmulator(); var decoder = new PacketDecoder();
    Packet Send(MessageType type, uint seq, byte[] body) => decoder.Feed(device.Receive(Wire.Encode(new Packet(type, seq, body)))).Single();
    Send(MessageType.Hello, 0, []);
    Check(Send(MessageType.Ping, 99, []).Type == MessageType.RequestFull);
    Send(MessageType.Hello, 100, []);
    Check(Send(MessageType.FrameBegin, 101, [1, 0, 0, 0, 0]).Type == MessageType.RequestFull);
    Send(MessageType.Hello, 200, []);
    Send(MessageType.FrameBegin, 201, [1, 0, 0, 0, 1]);
    Check(Send(MessageType.FrameEnd, 202, [1, 0, 0, 0]).Type == MessageType.RequestFull);
    Send(MessageType.Hello, 300, []);
    Send(MessageType.FrameBegin, 301, [2, 0, 0, 0, 1]);
    var invalid = new PixelBlock(240, 0, 16, 16, new byte[512]).ToPayload();
    Check(Send(MessageType.Pixels, 302, invalid).Type == MessageType.RequestFull);
    Equal(0, device.CommittedFrames); Check(device.Pixels.All(b => b == 0));
}
static void ProjectStorage()
{
    string path = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".json");
    try
    {
        var originalPage = PageTemplates.Clock("Привет, дисплей");
        var page = originalPage with { DurationSeconds = 7, Layers = [originalPage.Layers[0] with { FontFamily = "Consolas" }, .. originalPage.Layers.Skip(1)] };
        var project = new DisplayProject { Pages = [page, PageTemplates.Text() with { Enabled = false }] };
        project.Save(path);
        var loaded = DisplayProject.Load(path);
        Check(System.Text.Json.JsonSerializer.Serialize(project) == System.Text.Json.JsonSerializer.Serialize(loaded));
        loaded.Save(path); // Replacing an existing file must also preserve the project.
        foreach (var invalid in new[]
        {
            project with { Version = 9 }, project with { FramesPerSecond = 21 }, project with { Pages = [] },
            project with { Pages = [page with { Background = "oops" }] },
            project with { Pages = [page with { DurationSeconds = 0 }] },
            project with { Pages = [page with { DurationSeconds = 3601 }] },
            project with { Pages = [page with { Enabled = false }] },
            project with { Pages = [page, page] },
            project with { Pages = [page with { Layers = [page.Layers[0] with { X = 240 }] }] },
            project with { Pages = [page with { Layers = [page.Layers[0] with { FontFamily = "" }] }] }
        })
        {
            try { invalid.Validate(); throw new Exception("Invalid project accepted"); }
            catch (InvalidDataException) { }
        }
    }
    finally { File.Delete(path); }
}
static void PageTiming()
{
    var first = PageTemplates.Clock() with { DurationSeconds = 2 };
    var skipped = PageTemplates.Text() with { DurationSeconds = 1, Enabled = false };
    var second = PageTemplates.Calibration() with { DurationSeconds = 3 };
    var project = new DisplayProject { Pages = [first, skipped, second] };
    var playback = new PagePlayback();
    Equal(first.Id, playback.Update(project, TimeSpan.Zero).Id);
    Equal(first.Id, playback.Update(project, TimeSpan.FromMilliseconds(1999)).Id);
    Equal(second.Id, playback.Update(project, TimeSpan.FromSeconds(2)).Id);
    Equal(3, playback.RemainingSeconds(second, TimeSpan.FromSeconds(2)));
    Equal(first.Id, playback.Update(project, TimeSpan.FromSeconds(5)).Id);
    Equal(second.Id, playback.Update(project, TimeSpan.FromSeconds(1000002)).Id);
    project = project with { Pages = [second, first] };
    playback.Select(second.Id, TimeSpan.Zero);
    Equal(first.Id, playback.Update(project, TimeSpan.FromSeconds(3)).Id);
    playback.Update(project with { AutoRotate = false }, TimeSpan.FromSeconds(200));
    Equal(first.Id, playback.Update(project, TimeSpan.FromSeconds(201)).Id);
    Equal(second.Id, playback.Update(project, TimeSpan.FromSeconds(202)).Id);
    Equal(first.Id, playback.Update(project with { Pages = [first] }, TimeSpan.FromSeconds(203)).Id);
    playback.Select(first.Id, TimeSpan.FromSeconds(300));
    Equal(first.Id, playback.Update(project, TimeSpan.FromSeconds(10)).Id); // elapsed clock reset.
}
static void ProjectMigration()
{
    string path = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".json");
    try
    {
        File.WriteAllText(path, """
            { "Version": 1, "Title": "Старый экран", "Accent": "#FF7878", "Background": "#000000", "FramesPerSecond": 10, "AutoRotate": false }
            """);
        var migrated = DisplayProject.Load(path);
        Equal(3, migrated.Version); Equal(2, migrated.Pages.Length); Equal(10, migrated.FramesPerSecond);
        Check(!migrated.AutoRotate); Equal("Старый экран", migrated.Pages[0].Layers[0].Text);
        Equal("#FF7878", migrated.Pages[0].Layers[0].Color); Equal("#000000", migrated.Pages[0].Background);
        var original = migrated.Pages[0]; var copy = original.Duplicate();
        Check(original.Id != copy.Id && original.Layers.Zip(copy.Layers).All(pair => pair.First.Id != pair.Second.Id));
        copy = copy with { Layers = [copy.Layers[0] with { Text = "Копия" }, .. copy.Layers.Skip(1)] };
        Equal("Старый экран", original.Layers[0].Text);
        new DisplayProject { Pages = [original, copy] }.Validate();
        var weather = WidgetTemplates.Weather();
        var footer = new PageLayer { Type = LayerType.Text, Name = "Open-Meteo", Text = "Open-Meteo", X = 16, Y = 228, FontSize = 9 };
        new DisplayProject { Pages = [weather with { Layers = [.. weather.Layers, footer] }] }.Save(path);
        var upgraded = DisplayProject.Load(path).Pages[0]; Equal(weather.Layers.Length,upgraded.Layers.Length); Check(upgraded.Layers.All(layer=>layer.Id!=footer.Id));
        File.WriteAllText(path, "{ \"Version\": 20 }");
        try { DisplayProject.Load(path); throw new Exception("Unknown version accepted"); } catch (InvalidDataException) { }
    }
    finally { File.Delete(path); }
}
static async Task Stress()
{
    var random = new Random(33); var link = new EmulatorLink();
    await using var session = new DisplaySession(link); await session.ConnectAsync(default);
    var frame = new byte[FrameDiffer.FrameBytes];
    for (int i = 0; i < 250; i++)
    {
        for (int mutation = 0; mutation < 10; mutation++) frame[random.Next(frame.Length)] = (byte)random.Next(256);
        if (i % 37 == 0) await session.ConnectAsync(default);
        await session.SendFrameAsync(frame, default); Same(frame, link.Device.Pixels);
    }
}

static async Task NativeRoundTrip()
{
    string? executable = Environment.GetEnvironmentVariable("MD_NATIVE_BRIDGE");
    if (string.IsNullOrEmpty(executable))
    {
        Console.WriteLine("      native bridge not configured; set MD_NATIVE_BRIDGE to test_device executable");
        return;
    }
    string dump = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".rgb565");
    var frame = new byte[FrameDiffer.FrameBytes];
    try
    {
        var link = new NativeLink(executable, dump);
        await using (var session = new DisplaySession(link))
        {
            await session.ConnectAsync(default);
            var random = new Random(18); random.NextBytes(frame);
            Check(session.Capabilities!.SupportsBrightness);
            Check(await session.SetBrightnessAsync(0, default));
            await session.SendFrameAsync(frame, default);
            for (int i = 0; i < 100; i++)
            {
                frame[random.Next(frame.Length)] ^= 0xff;
                await session.SendFrameAsync(frame, default);
            }
            frame[0] ^= 0xff; link.CorruptNextPixels = true;
            try { await session.SendFrameAsync(frame, default); throw new Exception("Native receiver accepted bad CRC"); }
            catch (FullFrameRequiredException) { }
            await session.ConnectAsync(default);
            Check(await session.SetBrightnessAsync(37, default));
            await session.SendFrameAsync(frame, default);
        }
        Same(frame, File.ReadAllBytes(dump));
    }
    finally { File.Delete(dump); }
}

sealed class WeatherHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => respond(request,cancellation);
}

sealed class NativeLink : IDeviceLink
{
    private readonly Process process;
    public bool CorruptNextPixels { get; set; }
    public NativeLink(string executable, string dump)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("--bridge"); start.ArgumentList.Add(dump);
        process = Process.Start(start) ?? throw new IOException("Could not start firmware bridge");
    }
    public async ValueTask WriteAsync(byte[] data, CancellationToken token)
    {
        if (CorruptNextPixels && data[5] == (byte)MessageType.Pixels)
        {
            data = (byte[])data.Clone(); data[^1] ^= 0x80; CorruptNextPixels = false;
        }
        await process.StandardInput.BaseStream.WriteAsync(data, token);
        await process.StandardInput.BaseStream.FlushAsync(token);
    }
    public async ValueTask<byte[]> ReadAsync(CancellationToken token)
    {
        var data = new byte[1024];
        int count = await process.StandardOutput.BaseStream.ReadAsync(data, token);
        if (count == 0) throw new IOException("Firmware bridge closed stdout");
        return data.AsSpan(0, count).ToArray();
    }
    public async ValueTask DisposeAsync()
    {
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync(timeout.Token); CheckExit(); }
        finally { if (!process.HasExited) process.Kill(); process.Dispose(); }
    }
    private void CheckExit() { if (process.ExitCode != 0) throw new IOException("Firmware bridge failed: " + process.ExitCode); }
}

sealed class DroppingLink : IDeviceLink
{
    private readonly EmulatorLink inner = new();
    private bool suppress;
    public bool DropFrameEnd { get; set; }
    public async ValueTask WriteAsync(byte[] data, CancellationToken token)
    {
        await inner.WriteAsync(data, token);
        suppress = DropFrameEnd && data[5] == (byte)MessageType.FrameEnd;
        if (suppress) { DropFrameEnd = false; _ = await inner.ReadAsync(token); }
    }
    public async ValueTask<byte[]> ReadAsync(CancellationToken token)
    {
        if (suppress) await Task.Delay(Timeout.Infinite, token);
        return await inner.ReadAsync(token);
    }
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
