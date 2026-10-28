using System.Buffers.Binary;

namespace MiniDisplay.Core;

public sealed class FullFrameRequiredException : IOException
{
    public FullFrameRequiredException() : base("Устройство запросило полный кадр.") { }
}

// A single owner calls this class. One packet is outstanding, so ESP32 needs no frame queue.
public sealed class DisplaySession : IAsyncDisposable
{
    private readonly IDeviceLink link;
    private readonly PacketDecoder decoder = new();
    private readonly Queue<Packet> received = new();
    private readonly FrameDiffer differ = new();
    private uint sequence;
    private uint frameId;
    private bool full = true;
    public DeviceCapabilities? Capabilities { get; private set; }
    public long BytesSent { get; private set; }
    public int FramesSent { get; private set; }
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public DisplaySession(IDeviceLink link) => this.link = link;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        received.Clear();
        var response = await ExchangeAsync(MessageType.Hello, [], MessageType.Capabilities, cancellationToken);
        Capabilities = DeviceCapabilities.Parse(response.Payload);
        differ.Invalidate(); full = true;
        await SyncTimeAsync(cancellationToken);
    }

    public async Task SyncTimeAsync(CancellationToken cancellationToken)
    {
        // Local wall clock as a Unix-like second count; firmware never needs timezone rules.
        var body = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(body, (long)(DateTime.Now - DateTime.UnixEpoch).TotalSeconds);
        await ExchangeAsync(MessageType.TimeSync, body, MessageType.Ack, cancellationToken);
    }

    public void RequestFullFrame() { differ.Invalidate(); full = true; }

    public async Task<bool> SendFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        if (Capabilities is null) throw new InvalidOperationException("Сначала подключитесь к устройству.");
        var blocks = differ.Compare(frame, Math.Min((int)Capabilities.MaxPayload, Wire.MaxPayload));
        if (blocks.Count == 0) return false;
        uint currentId = unchecked(++frameId);
        var begin = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(begin, currentId);
        begin[4] = full ? (byte)1 : (byte)0;
        try
        {
            await ExchangeAsync(MessageType.FrameBegin, begin, MessageType.Ack, cancellationToken);
            foreach (var block in blocks)
                await ExchangeAsync(MessageType.Pixels, block.ToPayload(), MessageType.Ack, cancellationToken);
            var end = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(end, currentId);
            await ExchangeAsync(MessageType.FrameEnd, end, MessageType.Ack, cancellationToken);
            differ.Acknowledge(frame); full = false; FramesSent++;
            return true;
        }
        catch
        {
            // A partial/uncertain frame can never become a delta baseline.
            RequestFullFrame();
            throw;
        }
    }

    public async Task PingAsync(CancellationToken cancellationToken) =>
        _ = await ExchangeAsync(MessageType.Ping, [], MessageType.Ack, cancellationToken);

    private async Task<Packet> ExchangeAsync(MessageType type, byte[] payload, MessageType reply, CancellationToken cancellationToken)
    {
        uint sentSequence = sequence++;
        var data = Wire.Encode(new Packet(type, sentSequence, payload));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResponseTimeout);
        try
        {
            await link.WriteAsync(data, timeout.Token);
            BytesSent += data.Length;
            while (true)
            {
                if (received.Count == 0)
                    foreach (var packet in decoder.Feed(await link.ReadAsync(timeout.Token))) received.Enqueue(packet);
                while (received.TryDequeue(out var packet))
                {
                    if (packet.Type == MessageType.RequestFull) throw new FullFrameRequiredException();
                    if (packet.Sequence == sentSequence && packet.Type == reply) return packet;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Устройство не ответило за отведённое время.");
        }
    }

    public ValueTask DisposeAsync() => link.DisposeAsync();
}
