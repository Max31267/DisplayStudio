using System.Buffers.Binary;

namespace MiniDisplay.Core;

public sealed record PixelBlock(ushort X, ushort Y, ushort Width, ushort Height, byte[] Pixels)
{
    public byte[] ToPayload()
    {
        var payload = new byte[8 + Pixels.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, X);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), Y);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), Width);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), Height);
        Pixels.CopyTo(payload, 8);
        return payload;
    }
}

public sealed class FrameDiffer
{
    public const int FrameBytes = Wire.Width * Wire.Height * 2;
    private byte[]? acknowledged;

    public void Invalidate() => acknowledged = null;

    public IReadOnlyList<PixelBlock> Compare(byte[] frame, int maxPayload = Wire.MaxPayload)
    {
        if (frame.Length != FrameBytes) throw new ArgumentException("Expected 240×240 RGB565.", nameof(frame));
        if (maxPayload < 520) throw new ArgumentOutOfRangeException(nameof(maxPayload));
        var blocks = new List<PixelBlock>();
        int maxTiles = Math.Min(15, (maxPayload - 8) / (16 * 16 * 2));
        for (int y = 0; y < Wire.Height; y += 16)
        {
            for (int x = 0; x < Wire.Width;)
            {
                if (!Changed(frame, x, y)) { x += 16; continue; }
                int start = x;
                x += 16;
                while (x < Wire.Width && (x - start) / 16 < maxTiles && Changed(frame, x, y)) x += 16;
                int width = x - start;
                var pixels = new byte[width * 16 * 2];
                for (int row = 0; row < 16; row++)
                    frame.AsSpan(((y + row) * Wire.Width + start) * 2, width * 2).CopyTo(pixels.AsSpan(row * width * 2));
                blocks.Add(new PixelBlock((ushort)start, (ushort)y, (ushort)width, 16, pixels));
            }
        }
        return blocks;
    }

    public void Acknowledge(byte[] frame)
    {
        if (frame.Length != FrameBytes) throw new ArgumentException("Expected 240×240 RGB565.", nameof(frame));
        acknowledged = (byte[])frame.Clone();
    }

    private bool Changed(byte[] frame, int x, int y)
    {
        if (acknowledged is null) return true;
        for (int row = 0; row < 16; row++)
        {
            int offset = ((y + row) * Wire.Width + x) * 2;
            if (!frame.AsSpan(offset, 32).SequenceEqual(acknowledged.AsSpan(offset, 32))) return true;
        }
        return false;
    }
}
