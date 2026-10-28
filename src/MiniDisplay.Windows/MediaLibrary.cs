using MiniDisplay.Core;
using SkiaSharp;

namespace MiniDisplay.Windows;

public sealed class MediaLibrary : IDisposable
{
    private readonly Dictionary<string, MediaImage> decoded = [];
    private readonly HashSet<string> invalid = [];
    private ProjectAsset[] assets = [];
    public void SetAssets(ProjectAsset[] value)
    {
        if (ReferenceEquals(assets, value)) return;
        assets = value;
        invalid.RemoveWhere(key => !assets.Any(asset => asset.Id == key));
        foreach (var key in decoded.Keys.Where(key => !assets.Any(asset => asset.Id == key)).ToArray()) { decoded[key].Dispose(); decoded.Remove(key); }
    }
    public static void Validate(ProjectAsset asset) { asset.Validate(); using var image = new MediaImage(asset.Data); image.Frame(TimeSpan.Zero, 20, 0); }
    public SKBitmap? Get(PageLayer layer, TimeSpan elapsed)
    {
        if (invalid.Contains(layer.AssetId)) return null;
        if (!decoded.TryGetValue(layer.AssetId, out var image))
        {
            var asset = assets.FirstOrDefault(asset => asset.Id == layer.AssetId); if (asset is null) return null;
            // Bounded native buffers; independent render contexts have independent animation state.
            if (decoded.Count >= 2) { var oldest = decoded.First(); oldest.Value.Dispose(); decoded.Remove(oldest.Key); }
            try { decoded[layer.AssetId] = image = new MediaImage(asset.Data); } catch (InvalidDataException) { invalid.Add(layer.AssetId); return null; }
        }
        try { return image.Frame(layer.Type == LayerType.Gif ? elapsed : TimeSpan.Zero, layer.AnimationFps, layer.RepeatCount); }
        catch (InvalidDataException) { image.Dispose(); decoded.Remove(layer.AssetId); invalid.Add(layer.AssetId); return null; }
    }
    public void Dispose() { foreach (var image in decoded.Values) image.Dispose(); decoded.Clear(); }
}

public sealed class MediaImage : IDisposable
{
    private readonly SKData data;
    private readonly SKCodec codec;
    private readonly SKBitmap bitmap;
    private readonly SKCodecFrameInfo[] frames;
    private readonly long[] ends;
    private int current = -1;
    public int Width => bitmap.Width;
    public int Height => bitmap.Height;
    public bool Animated => frames.Length > 1;
    public MediaImage(byte[] bytes)
    {
        data = SKData.CreateCopy(bytes);
        var created = SKCodec.Create(data);
        if (created is null) { data.Dispose(); throw new InvalidDataException("Не удалось прочитать изображение."); }
        codec = created;
        var info = codec.Info;
        frames = codec.FrameInfo;
        if (frames.Length > 4000) { codec.Dispose(); data.Dispose(); throw new InvalidDataException("В GIF допускается до 4000 кадров."); }
        long limit = frames.Length > 1 ? 4 * 1024 * 1024 : 36 * 1024 * 1024;
        if (info.Width < 1 || info.Height < 1 || info.Width > 32768 || info.Height > 32768 || (long)info.Width * info.Height > limit)
        { codec.Dispose(); data.Dispose(); throw new InvalidDataException("Слишком большое изображение: до 36 мегапикселей для фото и до 4 для GIF."); }
        if (frames.Length > 1) bitmap = new SKBitmap(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        else
        {
            float scale = Math.Min(1f, 1024f / Math.Max(info.Width, info.Height)); var size = codec.GetScaledDimensions(scale);
            var decoded = new SKBitmap(Math.Max(1,size.Width), Math.Max(1,size.Height), SKColorType.Bgra8888, SKAlphaType.Premul);
            var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
            if (result != SKCodecResult.Success) { decoded.Dispose(); codec.Dispose(); data.Dispose(); throw new InvalidDataException("Не удалось декодировать изображение: " + result); }
            if (Math.Max(decoded.Width, decoded.Height) <= 1024) bitmap = decoded;
            else
            {
                bitmap = new SKBitmap(Math.Max(1,(int)(info.Width * scale)), Math.Max(1,(int)(info.Height * scale)), SKColorType.Bgra8888, SKAlphaType.Premul);
                using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.Transparent); using var image = SKImage.FromBitmap(decoded);
                canvas.DrawImage(image, SKRect.Create(0,0,bitmap.Width,bitmap.Height), new SKSamplingOptions(SKFilterMode.Linear)); decoded.Dispose();
            }
            current = 0;
        }
        ends = new long[Math.Max(1, frames.Length)];
        for (int i = 0; i < ends.Length; i++) ends[i] = (i == 0 ? 0 : ends[i - 1]) + (frames.Length == 0 ? 100 : Math.Clamp(frames[i].Duration == 0 ? 100 : frames[i].Duration, 20, 60000));
    }
    public SKBitmap Frame(TimeSpan elapsed, int fps, int repeat)
    {
        double quantized = Math.Floor(Math.Max(0, elapsed.TotalMilliseconds) * fps / 1000) * 1000 / fps;
        long duration = ends[^1];
        int target;
        if (repeat > 0 && quantized >= duration * (double)repeat) target = ends.Length - 1;
        else
        {
            long time = (long)(quantized % duration);
            target = Array.FindIndex(ends, end => time < end); if (target < 0) target = 0;
        }
        if (target == current) return bitmap;
        var chain = new Stack<int>(); int required = target;
        while (required >= 0 && required != current)
        {
            chain.Push(required);
            int previous = frames.Length == 0 ? -1 : frames[required].RequiredFrame;
            if (previous >= required) throw new InvalidDataException("Некорректные зависимости кадров GIF.");
            required = previous;
        }
        int prior = required;
        if (prior < 0) bitmap.Erase(SKColors.Transparent);
        while (chain.TryPop(out int frame))
        {
            var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels(), new SKCodecOptions(frame, prior));
            if (result != SKCodecResult.Success) throw new InvalidDataException("Повреждён кадр изображения: " + result);
            prior = frame;
        }
        current = target; return bitmap;
    }
    public void Dispose() { bitmap.Dispose(); codec.Dispose(); data.Dispose(); }
}
