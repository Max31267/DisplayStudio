using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace MiniDisplay.Windows;

public sealed class EditorSurface
{
    public WriteableBitmap? Source { get; private set; }
    public void Update(SKBitmap frame)
    {
        if (Source is null || Source.PixelWidth != frame.Width || Source.PixelHeight != frame.Height)
            Source = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
        Source.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.GetPixels(), frame.ByteCount, frame.RowBytes);
    }
}
