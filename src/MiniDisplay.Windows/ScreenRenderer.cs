using System.Globalization;
using MiniDisplay.Core;
using SkiaSharp;

namespace MiniDisplay.Windows;

public sealed class ScreenRenderer : IDisposable
{
    private readonly SKBitmap bitmap = new(240, 240, SKColorType.Bgra8888, SKAlphaType.Opaque);
    private SKBitmap? editorBitmap, playbackBitmap;
    private readonly SKTypeface typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal);
    private readonly SKTypeface bold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
    private readonly Dictionary<string, SKTypeface> fontFaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly CultureInfo culture = CultureInfo.GetCultureInfo("ru-RU");
    private readonly MediaLibrary deviceMedia = new(), editorMedia = new(), thumbnailMedia = new(), playbackMedia = new();
    private byte[]? artworkBytes;
    private SKBitmap? artwork;
    public ProjectAsset[] Assets { set { deviceMedia.SetAssets(value); editorMedia.SetAssets(value); thumbnailMedia.SetAssets(value); playbackMedia.SetAssets(value); } }
    public SourceSnapshot Data { get; set; } = new();

    public byte[] Render(DisplayPage page, DateTime now, Guid? selected = null, bool grid = false, TimeSpan elapsed = default)
    {
        using var canvas = new SKCanvas(bitmap);
        DrawPage(canvas, page, now, selected, grid, elapsed, deviceMedia);
        var source = bitmap.Bytes;
        var result = new byte[FrameDiffer.FrameBytes];
        for (int i = 0; i < 240 * 240; i++)
        {
            int p = i * 4;
            ushort rgb = (ushort)(((source[p + 2] >> 3) << 11) | ((source[p + 1] >> 2) << 5) | (source[p] >> 3));
            result[i * 2] = (byte)(rgb >> 8); result[i * 2 + 1] = (byte)rgb;
        }
        return result;
    }

    // Draws the same layout at native viewport resolution, without upscaling a 240px frame.
    // The bitmap remains owned by this renderer and is valid until the next render/resize.
    public SKBitmap RenderEditor(DisplayPage page, DateTime now, int pixels, Guid? selected = null, bool grid = false, TimeSpan elapsed = default, bool thumbnail = false)
        => RenderScaled(ref editorBitmap, page, now, pixels, selected, grid, elapsed, thumbnail ? thumbnailMedia : editorMedia);

    public SKBitmap RenderPlayback(DisplayPage page, DateTime now, int pixels, TimeSpan elapsed = default)
        => RenderScaled(ref playbackBitmap, page, now, pixels, null, false, elapsed, playbackMedia);

    private SKBitmap RenderScaled(ref SKBitmap? target, DisplayPage page, DateTime now, int pixels, Guid? selected, bool grid, TimeSpan elapsed, MediaLibrary media)
    {
        if (pixels is < 240 or > 2048) throw new ArgumentOutOfRangeException(nameof(pixels));
        if (target is null || target.Width != pixels)
        {
            target?.Dispose();
            target = new SKBitmap(pixels, pixels, SKColorType.Bgra8888, SKAlphaType.Opaque);
        }
        using var canvas = new SKCanvas(target);
        canvas.Scale(pixels / 240f);
        DrawPage(canvas, page, now, selected, grid, elapsed, media);
        return target;
    }

    private void DrawPage(SKCanvas canvas, DisplayPage page, DateTime now, Guid? selected, bool grid, TimeSpan elapsed, MediaLibrary media)
    {
        canvas.Clear(SKColor.Parse(page.Background));
        foreach (var layer in page.Layers.Where(layer => layer.Visible))
        {
            var color = SKColor.Parse(layer.Color);
            if (layer.Type is LayerType.Image or LayerType.Gif or LayerType.Artwork)
            {
                SKBitmap? image;
                if (layer.Type == LayerType.Artwork)
                {
                    if (!ReferenceEquals(artworkBytes, Data.Artwork))
                    {
                        artworkBytes = Data.Artwork; artwork?.Dispose(); artwork = null;
                        if (artworkBytes is not null)
                            try { using var cover = new MediaImage(artworkBytes); artwork = cover.Frame(TimeSpan.Zero, 20, 0).Copy(); } catch (InvalidDataException) { }
                    }
                    image = artwork;
                }
                else image = media.Get(layer, elapsed);
                DrawImage(canvas, layer, image);
            }
            else if (layer.Type is LayerType.Bar or LayerType.Ring or LayerType.Graph) DrawIndicator(canvas, layer, color);
            else if (layer.Type == LayerType.WeatherIcon) DrawWeather(canvas, layer);
            else if (layer.Type == LayerType.Calibration)
            {
                canvas.Save(); canvas.Translate(layer.X, layer.Y);
                canvas.Scale(layer.Width / 240f, layer.Height / 240f);
                Calibration(canvas, color, now); canvas.Restore();
            }
            else if (layer.Type == LayerType.SecondsBar)
            {
                using var paint = new SKPaint { Color = color, IsAntialias = true };
                canvas.DrawRoundRect(layer.X, layer.Y, layer.Width * now.Second / 59f, layer.Height, layer.Height / 2f, layer.Height / 2f, paint);
            }
            else
            {
                if (layer.Type == LayerType.DataText) { canvas.Save(); canvas.ClipRect(SKRect.Create(layer.X, layer.Y, layer.Width, layer.Height)); }
                using var font = Font(layer);
                using var paint = new SKPaint { Color = color, IsAntialias = true };
                float baseline = layer.Y - font.Metrics.Ascent;
                foreach (string line in Content(layer, now).Split('\n'))
                {
                    string text = line.TrimEnd('\r');
                    if (layer.Type == LayerType.DataText) text = FitText(text, font, layer.Width);
                    canvas.DrawText(text, layer.X, baseline, SKTextAlign.Left, font, paint);
                    baseline += font.Spacing;
                }
                if (layer.Type == LayerType.DataText) canvas.Restore();
            }
        }
        if (grid)
        {
            using var paint = new SKPaint { Color = new SKColor(185, 192, 218, 42), StrokeWidth = 1, IsAntialias = true };
            for (int i = 16; i < 240; i += 16) { canvas.DrawLine(i + 0.5f, 0, i + 0.5f, 240, paint); canvas.DrawLine(0, i + 0.5f, 240, i + 0.5f, paint); }
        }
        if (selected is Guid id && page.Layers.FirstOrDefault(layer => layer.Id == id) is { Visible: true } chosen)
        {
            var bounds = Bounds(chosen, now);
            using var paint = new SKPaint { Color = new SKColor(49, 95, 255), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
            canvas.DrawRect(bounds, paint);
            if (LayerResize.Supported(chosen))
            {
                using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true };
                paint.IsAntialias = true;
                foreach (var anchor in LayerResize.Anchors(chosen))
                {
                    var marker = SKRect.Create((float)(anchor.X-LayerResize.MarkerSize/2),(float)(anchor.Y-LayerResize.MarkerSize/2),(float)LayerResize.MarkerSize,(float)LayerResize.MarkerSize);
                    canvas.DrawRect(marker,fill); canvas.DrawRect(marker,paint);
                }
            }
        }
        canvas.Flush();
    }

    public SKRect Bounds(PageLayer layer, DateTime now)
    {
        if (LayerResize.Supported(layer)) return SKRect.Create(layer.X,layer.Y,layer.Width,layer.Height);
        if (layer.Type is LayerType.SecondsBar or LayerType.Calibration or LayerType.Image or LayerType.Gif or LayerType.Artwork or LayerType.Bar or LayerType.Ring or LayerType.Graph or LayerType.WeatherIcon)
            return SKRect.Create(layer.X, layer.Y, layer.Width, Math.Max(6, layer.Height));
        if (layer.Type == LayerType.DataText) return SKRect.Create(layer.X, layer.Y, layer.Width, layer.Height);
        using var font = Font(layer);
        var lines = Content(layer, now).Split('\n');
        float width = Math.Max(8, lines.Max(line => font.MeasureText(line.TrimEnd('\r'))));
        if (layer.Type == LayerType.DataText) width = Math.Min(width, layer.Width);
        return SKRect.Create(layer.X, layer.Y, width, Math.Max(8, font.Spacing * lines.Length));
    }

    public PageLayer PlaceNewLayer(DisplayPage page, PageLayer layer, DateTime now)
    {
        var size = Bounds(layer, now);
        int width = Math.Min(240, (int)Math.Ceiling(size.Width)), height = Math.Min(240, (int)Math.Ceiling(size.Height));
        int left = width <= 216 ? 12 : 0, top = height <= 216 ? 12 : 0;
        int right = Math.Max(left, 240 - width - left), bottom = Math.Max(top, 240 - height - top);
        var occupied = page.Layers.Where(item => item.Visible).Select(item => Bounds(item, now)).ToArray();
        double best = double.PositiveInfinity;
        var placed = layer with { X = left, Y = top };
        for (int y = top; y <= bottom; y += 4)
        for (int x = left; x <= right; x += 8)
        {
            double overlap = 0;
            foreach (var bounds in occupied)
            {
                double w = Math.Max(0, Math.Min(x + width + 4, bounds.Right) - Math.Max(x - 4, bounds.Left));
                double h = Math.Max(0, Math.Min(y + height + 4, bounds.Bottom) - Math.Max(y - 4, bounds.Top));
                overlap += w * h;
            }
            if (overlap >= best) continue;
            best = overlap; placed = layer with { X = x, Y = y };
            if (overlap == 0) return placed;
        }
        return placed;
    }

    private SKFont Font(PageLayer layer)
    {
        SKTypeface face;
        if (string.Equals(layer.FontFamily, "Segoe UI", StringComparison.OrdinalIgnoreCase)) face = layer.Bold ? bold : typeface;
        else
        {
            string key = layer.FontFamily + (layer.Bold ? "|bold" : "|regular");
            if (!fontFaces.TryGetValue(key, out face!))
            {
                if (fontFaces.Count >= 64) { var oldest = fontFaces.First(); oldest.Value.Dispose(); fontFaces.Remove(oldest.Key); }
                face = SKTypeface.FromFamilyName(layer.FontFamily, layer.Bold ? SKFontStyle.Bold : SKFontStyle.Normal);
                fontFaces.Add(key, face);
            }
        }
        return new SKFont(face, layer.FontSize) { Edging = SKFontEdging.Antialias, Subpixel = true };
    }
    private string Content(PageLayer layer, DateTime now) => layer.Type switch
    {
        LayerType.Clock => now.ToString("HH:mm"), LayerType.Seconds => now.ToString("ss"),
        LayerType.Weekday => now.ToString("dddd", culture), LayerType.Date => now.ToString("d MMMM yyyy", culture),
        LayerType.DataText => layer.Text.Replace("{value}", Data.Get(layer.DataField).Text, StringComparison.Ordinal), _ => layer.Text
    };

    private static string FitText(string text, SKFont font, float width)
    {
        if (text.Length > 300) text = text[..300];
        if (font.MeasureText(text) <= width) return text;
        int low = 0, high = text.Length;
        while (low < high) { int middle = (low + high + 1) / 2; if (font.MeasureText(text[..middle] + "…") <= width) low = middle; else high = middle - 1; }
        return low == 0 ? "…" : text[..low] + "…";
    }
    private void DrawImage(SKCanvas canvas, PageLayer layer, SKBitmap? bitmap)
    {
        var destination = SKRect.Create(layer.X, layer.Y, layer.Width, layer.Height);
        if (bitmap is null)
        {
            using var fill = new SKPaint { Color = new SKColor(135, 153, 165, 30) }; canvas.DrawRoundRect(destination, 4, 4, fill);
            Text(canvas, layer.Type == LayerType.Artwork ? "Нет обложки" : "Нет ресурса", layer.X + 4, layer.Y + layer.Height / 2f, Math.Min(11, layer.Width / 8f), new SKColor(164, 185, 195)); return;
        }
        var source = new SKRect(bitmap.Width * layer.CropLeft / 100f, bitmap.Height * layer.CropTop / 100f, bitmap.Width * (100 - layer.CropRight) / 100f, bitmap.Height * (100 - layer.CropBottom) / 100f);
        float targetRatio = destination.Width / destination.Height, sourceRatio = source.Width / source.Height;
        if (layer.Fit == ImageFit.Cover)
        {
            if (sourceRatio > targetRatio) { float trim = (source.Width - source.Height * targetRatio) / 2; source.Left += trim; source.Right -= trim; }
            else { float trim = (source.Height - source.Width / targetRatio) / 2; source.Top += trim; source.Bottom -= trim; }
        }
        else if (layer.Fit == ImageFit.Contain)
        {
            if (sourceRatio > targetRatio) { float trim = (destination.Height - destination.Width / sourceRatio) / 2; destination.Top += trim; destination.Bottom -= trim; }
            else { float trim = (destination.Width - destination.Height * sourceRatio) / 2; destination.Left += trim; destination.Right -= trim; }
        }
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, source, destination, new SKSamplingOptions(SKFilterMode.Linear));
    }
    private void DrawIndicator(SKCanvas canvas, PageLayer layer, SKColor color)
    {
        var rect = SKRect.Create(layer.X, layer.Y, layer.Width, layer.Height);
        var reading = Data.Get(layer.DataField);
        float fraction = reading.Number is double number && double.IsFinite(number) ? (float)Math.Clamp((number - layer.RangeMin) / (layer.RangeMax - layer.RangeMin), 0, 1) : 0;
        using var background = new SKPaint { Color = color.WithAlpha(35), IsAntialias = true };
        using var foreground = new SKPaint { Color = color, IsAntialias = true };
        if (layer.Type == LayerType.Bar)
        {
            canvas.DrawRoundRect(rect, rect.Height / 2, rect.Height / 2, background);
            if (reading.Number.HasValue) canvas.DrawRoundRect(SKRect.Create(rect.Left, rect.Top, rect.Width * fraction, rect.Height), rect.Height / 2, rect.Height / 2, foreground);
        }
        else if (layer.Type == LayerType.Ring)
        {
            float stroke = Math.Max(2, Math.Min(rect.Width, rect.Height) * 0.07f); rect.Inflate(-stroke / 2, -stroke / 2);
            foreground.Style = background.Style = SKPaintStyle.Stroke; foreground.StrokeWidth = background.StrokeWidth = stroke; foreground.StrokeCap = SKStrokeCap.Round;
            canvas.DrawOval(rect, background); if (reading.Number.HasValue) canvas.DrawArc(rect, -90, 360 * fraction, false, foreground);
            using var font = new SKFont(bold, Math.Min(layer.Width, layer.Height) * 0.23f) { Edging = SKFontEdging.Antialias };
            foreground.Style = SKPaintStyle.Fill; string text = reading.Number.HasValue ? reading.Text : "—";
            canvas.DrawText(text, rect.MidX, rect.MidY - (font.Metrics.Ascent + font.Metrics.Descent) / 2, SKTextAlign.Center, font, foreground);
        }
        else
        {
            canvas.DrawRoundRect(rect, 3, 3, background);
            if (!Data.History.TryGetValue(layer.DataField, out var samples)) return;
            using var builder = new SKPathBuilder(); bool started = false;
            for (int i = 0; i < samples.Length; i++)
            {
                if (!double.IsFinite(samples[i])) { started = false; continue; }
                float x = rect.Right - (samples.Length - 1 - i) * rect.Width / 59;
                float y = rect.Bottom - (float)Math.Clamp((samples[i] - layer.RangeMin) / (layer.RangeMax - layer.RangeMin), 0, 1) * rect.Height;
                if (started) builder.LineTo(x, y); else builder.MoveTo(x, y); started = true;
            }
            using var path = builder.Detach(); foreground.Style = SKPaintStyle.Stroke; foreground.StrokeWidth = 1.5f; canvas.DrawPath(path, foreground);
        }
    }
    private void DrawWeather(SKCanvas canvas, PageLayer layer)
    {
        canvas.Save(); canvas.Translate(layer.X, layer.Y); canvas.Scale(layer.Width / 50f, layer.Height / 50f);
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(253, 224, 71) };
        if (Data.WeatherCode is null) Text(canvas, "—", 12, 30, 25, new SKColor(164, 185, 195));
        else
        {
            if (Data.WeatherCode <= 2) { canvas.DrawCircle(21, 20, 12, paint); paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 2; for (int i = 0; i < 8; i++) { double a = i * Math.PI / 4; canvas.DrawLine(21 + (float)Math.Cos(a) * 15, 20 + (float)Math.Sin(a) * 15, 21 + (float)Math.Cos(a) * 19, 20 + (float)Math.Sin(a) * 19, paint); } paint.Style = SKPaintStyle.Fill; }
            if (Data.WeatherCode > 0) { paint.Color = new SKColor(200, 214, 226); canvas.DrawRoundRect(8, 24, 36, 16, 8, 8, paint); canvas.DrawCircle(24, 23, 10, paint); canvas.DrawCircle(35, 27, 8, paint); }
            if (Data.WeatherCode >= 51) { paint.Color = new SKColor(100, 190, 255); paint.StrokeWidth = 2; for (int i = 0; i < 3; i++) canvas.DrawLine(15 + i * 11, 42, 12 + i * 11, 48, paint); }
        }
        canvas.Restore();
    }

    private void Calibration(SKCanvas canvas, SKColor accent, DateTime now)
    {
        Text(canvas, "ПРОВЕРКА ДИСПЛЕЯ", 14, 25, 12, SKColors.White, true);
        SKColor[] colors = [SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.White];
        for (int i = 0; i < colors.Length; i++)
        {
            using var paint = new SKPaint { Color = colors[i] };
            canvas.DrawRect(12 + i * 54, 42, 54, 63, paint);
        }
        for (int i = 0; i < 216; i++)
        {
            byte value = (byte)(i * 255 / 215);
            using var paint = new SKPaint { Color = new SKColor(value, value, value) };
            canvas.DrawRect(12 + i, 117, 1, 23, paint);
        }
        using var outline = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        canvas.DrawRect(0.5f, 0.5f, 239, 239, outline);
        canvas.DrawLine(110, 165, 130, 165, outline); canvas.DrawLine(120, 155, 120, 175, outline);
        Text(canvas, "ВЕРХ ↑", 14, 180, 12, SKColors.White);
        Text(canvas, "RGB · 240×240", 14, 216, 15, accent);
        using var dot = new SKPaint { Color = accent, IsAntialias = true };
        canvas.DrawCircle(18 + (float)(now.TimeOfDay.TotalSeconds % 3 / 3) * 204, 229, 3, dot);
    }

    private void Text(SKCanvas canvas, string text, float x, float y, float size, SKColor color, bool heavy = false)
    {
        using var font = new SKFont(heavy ? bold : typeface, size) { Edging = SKFontEdging.Antialias, Subpixel = true };
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);
    }

    public void Dispose() { bitmap.Dispose(); editorBitmap?.Dispose(); playbackBitmap?.Dispose(); artwork?.Dispose(); deviceMedia.Dispose(); editorMedia.Dispose(); thumbnailMedia.Dispose(); playbackMedia.Dispose(); foreach (var face in fontFaces.Values) face.Dispose(); typeface.Dispose(); bold.Dispose(); }
}
