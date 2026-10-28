using System.Buffers.Binary;
using System.Threading.Channels;

namespace MiniDisplay.Core;

// Protocol peer, not a second renderer. All visible pixels arrive through MDP1.
public sealed class DeviceEmulator
{
    private readonly PacketDecoder decoder = new();
    private uint expectedSequence;
    private bool connected;
    private bool frameOpen;
    private bool needsFull = true;
    private bool fullFrame;
    private uint frameId;
    private readonly bool[] covered = new bool[15 * 15];
    public byte[] Pixels { get; } = new byte[FrameDiffer.FrameBytes];
    public int CommittedFrames { get; private set; }
    public uint? LastFrameId { get; private set; }
    public int RejectedPackets => decoder.RejectedPackets;

    public byte[] Receive(ReadOnlySpan<byte> bytes)
    {
        var output = new List<byte>();
        decoder.Feed(bytes, packet => output.AddRange(Wire.Encode(Handle(packet))), () =>
        {
            frameOpen = false; needsFull = true;
            output.AddRange(Wire.Encode(new Packet(MessageType.RequestFull, expectedSequence, [])));
        });
        return output.ToArray();
    }

    private Packet Handle(Packet packet)
    {
        if (packet.Type == MessageType.Hello && packet.Payload.Length == 0)
        {
            connected = true; expectedSequence = unchecked(packet.Sequence + 1);
            frameOpen = false; needsFull = true;
            var caps = new byte[10];
            BinaryPrimitives.WriteUInt16LittleEndian(caps, Wire.Width);
            BinaryPrimitives.WriteUInt16LittleEndian(caps.AsSpan(2), Wire.Height);
            BinaryPrimitives.WriteUInt16LittleEndian(caps.AsSpan(4), Wire.MaxPayload);
            BinaryPrimitives.WriteUInt32LittleEndian(caps.AsSpan(6), 160 * 1024);
            return new Packet(MessageType.Capabilities, packet.Sequence, caps);
        }
        if (!connected || packet.Sequence != expectedSequence) return Reject(packet);
        expectedSequence = unchecked(expectedSequence + 1);
        var body = packet.Payload.AsSpan();
        bool valid;
        switch (packet.Type)
        {
            case MessageType.FrameBegin:
                valid = body.Length == 5 && !frameOpen && body[4] <= 1 && (!needsFull || body[4] == 1);
                if (valid)
                {
                    frameId = BinaryPrimitives.ReadUInt32LittleEndian(body);
                    fullFrame = body[4] == 1; frameOpen = true;
                    Array.Clear(covered);
                }
                break;
            case MessageType.Pixels:
                valid = frameOpen && Draw(body);
                break;
            case MessageType.FrameEnd:
                valid = frameOpen && body.Length == 4 && BinaryPrimitives.ReadUInt32LittleEndian(body) == frameId &&
                    (!fullFrame || covered.All(value => value));
                if (valid)
                {
                    frameOpen = false; needsFull = false;
                    CommittedFrames++; LastFrameId = frameId;
                }
                break;
            case MessageType.TimeSync: valid = !frameOpen && body.Length == 8; break;
            case MessageType.Ping: valid = !frameOpen && body.Length == 0; break;
            default: valid = false; break;
        }
        return valid ? new Packet(MessageType.Ack, packet.Sequence, []) : Reject(packet);
    }

    private bool Draw(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) return false;
        int x = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        int y = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
        int w = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
        int h = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]);
        if (w == 0 || h != 16 || x % 16 != 0 || y % 16 != 0 || w % 16 != 0 ||
            x + w > Wire.Width || y + h > Wire.Height || payload.Length != 8 + w * h * 2) return false;
        for (int row = 0; row < h; row++)
            payload.Slice(8 + row * w * 2, w * 2).CopyTo(Pixels.AsSpan(((y + row) * Wire.Width + x) * 2));
        for (int tile = x / 16; tile < (x + w) / 16; tile++) covered[(y / 16) * 15 + tile] = true;
        return true;
    }

    private Packet Reject(Packet packet)
    {
        frameOpen = false; needsFull = true;
        return new Packet(MessageType.RequestFull, packet.Sequence, []);
    }
}

public interface IDeviceLink : IAsyncDisposable
{
    ValueTask WriteAsync(byte[] data, CancellationToken cancellationToken);
    ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken);
}

public sealed class EmulatorLink : IDeviceLink
{
    private readonly Channel<byte[]> replies = Channel.CreateUnbounded<byte[]>();
    public DeviceEmulator Device { get; } = new();
    private int corruptNextWrite;
    private int disconnected;
    public bool CorruptNextWrite { get => Volatile.Read(ref corruptNextWrite) != 0; set => Volatile.Write(ref corruptNextWrite, value ? 1 : 0); }
    public bool Disconnected { get => Volatile.Read(ref disconnected) != 0; set => Volatile.Write(ref disconnected, value ? 1 : 0); }

    public ValueTask WriteAsync(byte[] data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Disconnected) throw new IOException("Эмулятор отключён.");
        if (Interlocked.Exchange(ref corruptNextWrite, 0) != 0)
        {
            data = (byte[])data.Clone();
            data[^1] ^= 0x80;
        }
        var response = Device.Receive(data);
        if (response.Length > 0) replies.Writer.TryWrite(response);
        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken) => replies.Reader.ReadAsync(cancellationToken);
    public ValueTask DisposeAsync() { replies.Writer.TryComplete(); return ValueTask.CompletedTask; }
}
