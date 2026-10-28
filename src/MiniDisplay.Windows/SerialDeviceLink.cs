using System.IO.Ports;
using MiniDisplay.Core;

namespace MiniDisplay.Windows;

public sealed class SerialDeviceLink : IDeviceLink
{
    private readonly SerialPort port;

    public SerialDeviceLink(string name)
    {
        port = new SerialPort(name, 921600) { ReadTimeout = 100, WriteTimeout = 1000, DtrEnable = true, RtsEnable = false };
        try { port.Open(); port.DiscardInBuffer(); }
        catch { port.Dispose(); throw; }
    }

    public async ValueTask WriteAsync(byte[] data, CancellationToken cancellationToken)
    {
        await Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); port.Write(data, 0, data.Length); }, cancellationToken);
    }

    public async ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            var buffer = new byte[1024];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { return buffer.AsSpan(0, port.Read(buffer, 0, buffer.Length)).ToArray(); }
                catch (TimeoutException) { }
            }
        }, cancellationToken);
    }

    public ValueTask DisposeAsync() { port.Dispose(); return ValueTask.CompletedTask; }
}
