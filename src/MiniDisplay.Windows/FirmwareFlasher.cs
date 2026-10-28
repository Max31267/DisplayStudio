using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using MiniDisplay.Core;

namespace MiniDisplay.Windows;

public static class FirmwareFlasher
{
    public static async Task Run(FlashCommand command,IProgress<string> log,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var start=new ProcessStartInfo(command.Executable) { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=Path.GetDirectoryName(command.Executable)! };
        foreach(var argument in command.Arguments) start.ArgumentList.Add(argument);
        using var process=Process.Start(start) ?? throw new IOException("Не удалось запустить прошивальщик.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        async Task Drain(StreamReader reader)
        {
            while(await reader.ReadLineAsync() is string line)
            {
                line=Regex.Replace(line,@"\x1B\[[0-?]*[ -/]*[@-~]","");
                if(line.Length>0) log.Report(line.Length>2000?line[..2000]:line);
            }
        }
        var output=Task.WhenAll(Drain(process.StandardOutput),Drain(process.StandardError));
        try { await process.WaitForExitAsync(timeout.Token); await output; if(process.ExitCode!=0) throw new IOException("Прошивка не завершена. Проверьте порт и режим BOOT; подробности в журнале."); }
        catch(OperationCanceledException)
        { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await output; if(cancellation.IsCancellationRequested) throw; throw new TimeoutException("Превышено время прошивки. Переведите ESP32-C3 в BOOT и повторите."); }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
}
