using System.Buffers.Binary;

namespace MiniDisplay.Core;

public enum MessageType : byte
{
    Hello = 1, Capabilities = 2, FrameBegin = 3, Pixels = 4, FrameEnd = 5,
    Ack = 6, RequestFull = 7, TimeSync = 8, Ping = 9
}

public sealed record Packet(MessageType Type, uint Sequence, byte[] Payload);

public static class Wire
{
    public const int Width = 240;
    public const int Height = 240;
    public const int HeaderSize = 16;
    public const int MaxPayload = 8 + Width * 16 * 2;
    public const byte Version = 1;
    public static ReadOnlySpan<byte> Magic => "MDP1"u8;

    public static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second = default)
    {
        uint crc = 0xffffffff;
        foreach (var b in first) crc = Step(crc, b);
        foreach (var b in second) crc = Step(crc, b);
        return ~crc;
    }

    private static uint Step(uint crc, byte b)
    {
        crc ^= b;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        return crc;
    }

    public static byte[] Encode(Packet packet)
    {
        if (packet.Payload.Length > MaxPayload) throw new ArgumentOutOfRangeException(nameof(packet));
        var data = new byte[HeaderSize + packet.Payload.Length];
        Magic.CopyTo(data);
        data[4] = Version;
        data[5] = (byte)packet.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), (ushort)packet.Payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), packet.Sequence);
        packet.Payload.CopyTo(data, HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), Crc32(data.AsSpan(4, 8), packet.Payload));
        return data;
    }
}

// Bounded storage; arbitrary serial fragmentation and boot log noise are allowed.
public sealed class PacketDecoder
{
    private readonly byte[] buffer = new byte[Wire.HeaderSize + Wire.MaxPayload];
    private int count;
    public int RejectedPackets { get; private set; }

    public IReadOnlyList<Packet> Feed(ReadOnlySpan<byte> bytes, Action<Packet>? onPacket = null, Action? onRejected = null)
    {
        var packets = new List<Packet>();
        foreach (var value in bytes)
        {
            buffer[count++] = value;
            while (count >= 4)
            {
                if (!buffer.AsSpan(0, 4).SequenceEqual(Wire.Magic)) { DropFirst(); continue; }
                if (count < Wire.HeaderSize) break;
                int length = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(6));
                if (buffer[4] != Wire.Version || length > Wire.MaxPayload)
                {
                    RejectedPackets++; onRejected?.Invoke(); DropFirst(); continue;
                }
                if (count < Wire.HeaderSize + length) break;
                var payload = buffer.AsSpan(Wire.HeaderSize, length);
                if (Wire.Crc32(buffer.AsSpan(4, 8), payload) != BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12)))
                {
                    RejectedPackets++; onRejected?.Invoke(); DropFirst(); continue;
                }
                var packet = new Packet((MessageType)buffer[5], BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8)), payload.ToArray());
                packets.Add(packet);
                onPacket?.Invoke(packet);
                int remaining = count - Wire.HeaderSize - length;
                buffer.AsSpan(Wire.HeaderSize + length, remaining).CopyTo(buffer);
                count = remaining;
            }
        }
        return packets;
    }

    private void DropFirst()
    {
        buffer.AsSpan(1, --count).CopyTo(buffer);
    }
}

public sealed record DeviceCapabilities(ushort Width, ushort Height, ushort MaxPayload, uint FreeHeap)
{
    public static DeviceCapabilities Parse(byte[] payload)
    {
        if (payload.Length != 10) throw new InvalidDataException("Некорректный ответ устройства.");
        var caps = new DeviceCapabilities(BinaryPrimitives.ReadUInt16LittleEndian(payload),
            BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2)),
            BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(6)));
        if (caps.Width != Wire.Width || caps.Height != Wire.Height || caps.MaxPayload < 520)
            throw new InvalidDataException("Устройство не поддерживает экран 240×240 и блоки 16×16.");
        return caps;
    }
}
