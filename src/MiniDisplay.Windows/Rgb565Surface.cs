using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiniDisplay.Core;

namespace MiniDisplay.Windows;

// Updated only on the dispatcher thread; avoids allocating a new WPF image every tick.
public sealed class Rgb565Surface
{
    private readonly byte[] pixels = new byte[240 * 240 * 4];
    public WriteableBitmap Source { get; } = new(240, 240, 96, 96, PixelFormats.Bgra32, null);
    public void Update(byte[] frame)
    {
        if (frame.Length != FrameDiffer.FrameBytes) throw new ArgumentException("Expected a 240×240 RGB565 frame.", nameof(frame));
        for (int i = 0; i < 240 * 240; i++)
        {
            int value = (frame[i * 2] << 8) | frame[i * 2 + 1];
            int r = (value >> 11) & 31, g = (value >> 5) & 63, b = value & 31;
            pixels[i * 4] = (byte)((b << 3) | (b >> 2));
            pixels[i * 4 + 1] = (byte)((g << 2) | (g >> 4));
            pixels[i * 4 + 2] = (byte)((r << 3) | (r >> 2)); pixels[i * 4 + 3] = 255;
        }
        Source.WritePixels(new Int32Rect(0, 0, 240, 240), pixels, 240 * 4, 0);
    }
}
