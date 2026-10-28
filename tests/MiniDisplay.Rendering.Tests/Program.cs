using MiniDisplay.Core;
using MiniDisplay.Windows;
using System.IO;

if(args.Length==1 && args[0]=="--flasher-stub") { Console.WriteLine("Writing at 0x00000000 (50 %)"); Console.WriteLine("Hash of data verified."); return; }
if(args.Length==1 && args[0]=="--flasher-stub-wait") { await Task.Delay(30000); return; }

using var renderer = new ScreenRenderer();
var now = new DateTime(2026, 10, 28, 13, 30, 15);
var page = PageTemplates.Clock();
var deviceBefore = renderer.Render(page, now);
var low = renderer.RenderEditor(page, now, 240).Bytes;
var high = renderer.RenderEditor(page, now, 960, page.Layers[0].Id, true);
Check(high.Width == 960 && high.Height == 960, "Editor must render at viewport resolution");
Check(deviceBefore.Length == 240 * 240 * 2, "Transport must remain 240×240 RGB565");
Check(deviceBefore.AsSpan().SequenceEqual(renderer.Render(page, now)), "Editor overlays must not enter device frames");
Console.WriteLine("PASS  independent high-resolution editor and 240×240 device frames");

// In a nearest-neighbour enlargement every 4×4 block would have one colour.
// The native-size renderer must have glyph detail inside those former source pixels.
var detailed = renderer.RenderEditor(page, now, 960).Bytes;
int variedBlocks = 0;
for (int y = 60; y < 135; y++)
for (int x = 15; x < 210; x++)
{
    int first = ((y * 4) * 960 + x * 4) * 4;
    bool varied = false;
    for (int dy = 0; dy < 4 && !varied; dy++)
    for (int dx = 0; dx < 4; dx++)
    {
        int offset = ((y * 4 + dy) * 960 + x * 4 + dx) * 4;
        if (!detailed.AsSpan(first, 3).SequenceEqual(detailed.AsSpan(offset, 3))) { varied = true; break; }
    }
    if (varied) variedBlocks++;
}
Check(variedBlocks > 100, "Glyphs were enlarged from a low-resolution frame instead of redrawn");
Check(low.Length == 240 * 240 * 4, "Low-size editor should still have full-colour BGRA pixels");
Console.WriteLine("PASS  glyphs have new detail beyond the original 240px raster");

var empty = new DisplayPage { Background = "#123456" };
var color = renderer.RenderEditor(empty, now, 480).GetPixel(0, 0);
Check(color.Red == 0x12 && color.Green == 0x34 && color.Blue == 0x56, "Editor colour was quantized to RGB565");
var bounds = renderer.Bounds(page.Layers[1], now);
renderer.RenderEditor(page, now, 1536);
Check(bounds == renderer.Bounds(page.Layers[1], now), "DPI changes must not alter logical selection bounds");
Check(deviceBefore.AsSpan().SequenceEqual(renderer.Render(page, now)), "Resizing must not change transport pixels");
Console.WriteLine("PASS  full-colour editor, stable hit testing and repeated resizing");
var shown = renderer.RenderPlayback(page, now, 960);
Check(shown.Width == 960 && shown.Height == 960 && detailed.AsSpan().SequenceEqual(shown.Bytes), "Playback preview must redraw the same sharp glyphs as the editor at viewport resolution");
var shownPixels = shown.Bytes;
renderer.RenderEditor(empty, now, 480);
Check(shownPixels.AsSpan().SequenceEqual(shown.Bytes), "Editing and thumbnail rendering must not overwrite the playback preview");
var shownColor = renderer.RenderPlayback(empty, now, 480).GetPixel(0,0);
Check(shownColor.Red == 0x12 && shownColor.Green == 0x34 && shownColor.Blue == 0x56, "Playback preview must retain full colour instead of enlarging RGB565 pixels");
Check(deviceBefore.AsSpan().SequenceEqual(renderer.Render(page, now)), "Playback preview size must not change transmitted 240x240 pixels");
Console.WriteLine("PASS  sharp full-colour playback preview independent of editor and device");
var text = PageTemplates.Text();
text = text with { Layers = [text.Layers[0] with { Text = "iii WWW 123", FontSize = 24, Bold = false }] };
var proportional = renderer.RenderEditor(text, now, 480).Bytes;
var monospace = text with { Layers = [text.Layers[0] with { FontFamily = "Consolas" }] };
var fixedWidth = renderer.RenderEditor(monospace, now, 480).Bytes;
Check(!proportional.AsSpan().SequenceEqual(fixedWidth), "Selected font family must change rendered glyphs");
Check(renderer.Bounds(text.Layers[0], now).Width != renderer.Bounds(monospace.Layers[0], now).Width, "Hit testing must use the selected font");
Console.WriteLine("PASS  font family changes rendering and selection bounds");
var incoming = new PageLayer { Text = "Новый текст", FontSize = 18 };
var placed = renderer.PlaceNewLayer(page, incoming, now);
var placedBounds = renderer.Bounds(placed, now);
Check(placed.Id == incoming.Id, "Placing an element must preserve its identity");
Check(placedBounds.Left >= 0 && placedBounds.Top >= 0 && placedBounds.Right <= 240 && placedBounds.Bottom <= 240, "New element must start inside the canvas");
foreach (var existing in page.Layers)
{
    var occupied = renderer.Bounds(existing, now);
    Check(placedBounds.Right <= occupied.Left || placedBounds.Left >= occupied.Right || placedBounds.Bottom <= occupied.Top || placedBounds.Top >= occupied.Bottom, "New text must use available space instead of covering existing elements");
}
var hidden = PageTemplates.Calibration();
hidden = hidden with { Layers = [hidden.Layers[0] with { Visible = false }] };
Check(renderer.PlaceNewLayer(hidden, incoming, now) == renderer.PlaceNewLayer(empty, incoming, now), "Hidden elements must not occupy insertion space");
var covered = renderer.PlaceNewLayer(PageTemplates.Calibration(), incoming, now);
Check(covered.X >= 0 && covered.Y >= 0 && covered.X < 240 && covered.Y < 240, "A full canvas must still accept a selectable element");
Console.WriteLine("PASS  new elements use available space and ignore hidden content");
var gifBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "disposal.gif"));
using (var gif = new MediaImage(gifBytes))
{
    Check(gif.Animated, "GIF must expose its animation");
    Check(gif.Frame(TimeSpan.Zero,20,0).GetPixel(1,1).Red == 255, "First GIF frame must decode");
    Check(gif.Frame(TimeSpan.FromMilliseconds(100),20,0).GetPixel(5,1).Blue == 255, "GIF must preserve per-frame delay");
    var third = gif.Frame(TimeSpan.FromMilliseconds(350),20,0);
    Check(third.GetPixel(5,1).Alpha == 0 && third.GetPixel(1,5).Green == 255 && third.GetPixel(1,1).Red == 255, "Restore-background disposal must erase the previous blue patch");
    var fourth = gif.Frame(TimeSpan.FromMilliseconds(500),20,0);
    Check(fourth.GetPixel(1,5).Alpha == 0 && fourth.GetPixel(5,5).Red == 255 && fourth.GetPixel(5,5).Green == 255, "Restore-previous disposal must remove the green patch");
    Check(gif.Frame(TimeSpan.FromMilliseconds(600),20,0).GetPixel(5,5).Alpha == 0, "Infinite playback must restart cleanly");
    Check(gif.Frame(TimeSpan.FromSeconds(5),20,1).GetPixel(5,5).Red == 255, "Finite playback must hold its final frame");
    Check(gif.Frame(TimeSpan.FromMilliseconds(149),5,0).GetPixel(5,1).Alpha == 0, "Frame-rate cap must skip frames without stretching the timeline");
}
Console.WriteLine("PASS  GIF transparency, variable delays, disposal, seeking, repeat and FPS cap");
var png = ProjectAsset.Create("alpha.png",File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixtures","alpha.png")));
renderer.Assets = [png];
var imageLayer = new PageLayer { Type = LayerType.Image, AssetId = png.Id, X = 20, Y = 20, Width = 100, Height = 100 };
var imagePage = new DisplayPage { Background = "#000000", Layers = [imageLayer] };
var contain = renderer.RenderEditor(imagePage, now,240);
Check(contain.GetPixel(40,25).Red == 0 && contain.GetPixel(40,70).Red == 255, "Contain must preserve aspect ratio and leave the background visible");
Check(contain.GetPixel(105,70).Blue is >= 120 and <= 135, "Transparent PNG must composite over the page background");
var cropped = renderer.RenderEditor(imagePage with { Layers = [imageLayer with { CropLeft = 50, Fit = ImageFit.Stretch }] },now,240);
Check(cropped.GetPixel(40,70).Red == 0 && cropped.GetPixel(40,70).Blue > 120, "Crop settings must apply before scaling");
var imageFrame = renderer.Render(imagePage,now);
var handles = renderer.RenderEditor(imagePage,now,240,imageLayer.Id);
foreach(var anchor in LayerResize.Anchors(imageLayer))
{
    var marker = handles.GetPixel((int)anchor.X,(int)anchor.Y);
    Check(marker.Red==255 && marker.Green==255 && marker.Blue==255,"Every image resize handle must be visible in the editor");
}
Check(imageFrame.AsSpan().SequenceEqual(renderer.Render(imagePage,now)),"Resize handles must never enter device frames");
Check(renderer.Bounds(imageLayer with { Height=1 },now).Height==1,"Image handles must use the real image box, including one-pixel heights");
Console.WriteLine("PASS  eight image resize handles are visible only in the editor");
renderer.RenderEditor(imagePage, now,960,imageLayer.Id,true);
Check(renderer.Render(new DisplayPage { Background = "#FF00FF" },now)[0] == 0xf8, "Previous media must not remain after changing pages");
Console.WriteLine("PASS  image fit, crop, alpha compositing and clean page changes");
renderer.Data = new SourceSnapshot { Values = new Dictionary<DataField,DataReading> { [DataField.CpuLoad] = new("75%",75) }, History = new Dictionary<DataField,double[]> { [DataField.CpuLoad] = [10,20,double.NaN,80,75] } };
foreach (var template in new[] { WidgetTemplates.Weather(),WidgetTemplates.Computer(),WidgetTemplates.Music(),WidgetTemplates.Codex() })
    Check(renderer.Render(template,now).Length == FrameDiffer.FrameBytes,"Every widget page must render with missing sources");
Console.WriteLine("PASS  live widgets and graphs tolerate unavailable sources");
var photoBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixtures","photo.png"));
using (var photo = new MediaImage(photoBytes))
    Check(photo.Width == 1024 && photo.Height == 512,"Large photographs must use a bounded rendering image while preserving aspect ratio");
var gifAsset = ProjectAsset.Create("disposal.gif",gifBytes);
renderer.Assets = [gifAsset];
var animatedPage = new DisplayPage { Background="#000000",Layers=[new() { Type=LayerType.Gif,AssetId=gifAsset.Id,X=0,Y=0,Width=8,Height=8 }] };
renderer.Render(animatedPage,now,elapsed:TimeSpan.FromMilliseconds(350));
renderer.RenderEditor(animatedPage,now,480,elapsed:TimeSpan.Zero);
renderer.RenderEditor(animatedPage,now,240,thumbnail:true);
renderer.RenderPlayback(animatedPage,now,720,elapsed:TimeSpan.FromMilliseconds(100));
var independent = renderer.Render(animatedPage,now,elapsed:TimeSpan.FromMilliseconds(500));
Check(independent[(5*240+1)*2]==0 && independent[(5*240+1)*2+1]==0,"Editor and thumbnails must not leave animation pixels in the device frame");
Console.WriteLine("PASS  bounded photo decoding and independent animation contexts");
Console.WriteLine("11/11 rendering checks passed");
string startupCommand=StartupRegistration.Command(@"C:\Program Files\Display Studio\MiniDisplay.exe");
Check(startupCommand=="\"C:\\Program Files\\Display Studio\\MiniDisplay.exe\" --autostart","Autostart must quote the executable and request tray startup");
string profile=Path.Combine(AppContext.BaseDirectory,"DisplayStudio-settings-"+Guid.NewGuid().ToString("N"));
string? previousProfile=Environment.GetEnvironmentVariable("DISPLAYSTUDIO_PROFILE_DIR");
try
{
    Environment.SetEnvironmentVariable("DISPLAYSTUDIO_PROFILE_DIR",profile);
    LocalPreferences.Update(value=>value with { CodexExecutable="codex.exe",LastProjectPath="example.mdisplay",LastPort="COM8" });
    LocalPreferences.Update(value=>value with { AutoConnect=false });
    var saved=LocalPreferences.Load(); Check(saved.CodexExecutable=="codex.exe" && saved.LastProjectPath=="example.mdisplay" && saved.LastPort=="COM8" && !saved.AutoConnect,"Updating one preference must preserve all other local settings");
}
finally{Environment.SetEnvironmentVariable("DISPLAYSTUDIO_PROFILE_DIR",previousProfile);if(Directory.Exists(profile) && Path.GetDirectoryName(Path.GetFullPath(profile))==Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))Directory.Delete(profile,true);}
Console.WriteLine("PASS  autostart quoting and independent local preferences");
var process=Environment.ProcessPath!; string assembly=System.Reflection.Assembly.GetExecutingAssembly().Location;
string[] StubArguments(string mode)=>Path.GetFileNameWithoutExtension(process).Equals("dotnet",StringComparison.OrdinalIgnoreCase)?[assembly,mode]:[mode];
var messages=new System.Collections.Concurrent.ConcurrentQueue<string>();
await FirmwareFlasher.Run(new FlashCommand(process,StubArguments("--flasher-stub")),new CaptureProgress(messages),default);
Check(messages.Any(line=>line.Contains("verified")),"Flasher must capture process output and complete");
using(var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
{ try{await FirmwareFlasher.Run(new FlashCommand(process,StubArguments("--flasher-stub-wait")),new CaptureProgress(messages),cancel.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){} }
Console.WriteLine("PASS  integrated flasher process output and cancellation without hardware");
Console.WriteLine("2/2 Windows integration checks passed");
if (args.Length == 2 && args[0] == "--write-example")
{
    var animated = ProjectAsset.Create("disposal.gif", gifBytes);
    var gallery = new DisplayPage { Name = "GIF и прозрачность", Layers = [new() { Name="GIF",Type=LayerType.Gif,AssetId=animated.Id,X=40,Y=40,Width=160,Height=160 }] };
    var picture = new DisplayPage { Name="PNG с прозрачностью",Layers=[new() { Name="PNG",Type=LayerType.Image,AssetId=png.Id,X=20,Y=20,Width=200,Height=200,Fit=ImageFit.Contain }] };
    new DisplayProject { Assets=[png,animated],Pages=[gallery,WidgetTemplates.Computer(),WidgetTemplates.Codex(),WidgetTemplates.Weather(),WidgetTemplates.Music(),picture] }.Save(args[1]);
    Console.WriteLine("WROTE portable example: " + Path.GetFullPath(args[1]));
}

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
sealed class CaptureProgress(System.Collections.Concurrent.ConcurrentQueue<string> messages) : IProgress<string>
{ public void Report(string value)=>messages.Enqueue(value); }
