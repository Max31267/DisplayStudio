using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace MiniDisplay.Windows;

public partial class App : System.Windows.Application
{
    private Mutex? instance;
    private readonly CancellationTokenSource lifetime=new();
    private string pipeName="";
    protected override void OnStartup(StartupEventArgs e)
    {
        string identity=Environment.UserDomainName+"\\"+Environment.UserName+"|"+Environment.GetEnvironmentVariable("DISPLAYSTUDIO_PROFILE_DIR");
        pipeName="DisplayStudio."+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
        instance=new Mutex(true,@"Local\"+pipeName,out bool first);
        if(!first)
        {
            if(!e.Args.Contains("--autostart",StringComparer.OrdinalIgnoreCase))
            {
                string path=e.Args.FirstOrDefault(value=>value.EndsWith(".mdisplay",StringComparison.OrdinalIgnoreCase)||value.EndsWith(".json",StringComparison.OrdinalIgnoreCase))??"";
                try
                {
                    using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.Out);
                    pipe.Connect(1500); byte[] message=Encoding.UTF8.GetBytes(path);
                    if(message.Length<=16000) { using var writer=new BinaryWriter(pipe,Encoding.UTF8,true); writer.Write(message.Length); writer.Write(message); writer.Flush(); }
                }
                catch(Exception ex) when(ex is IOException or TimeoutException) { MessageBox.Show("Display Studio уже работает. Откройте его через значок возле часов.","Display Studio"); }
            }
            Shutdown(); return;
        }
        _=Task.Run(Listen); base.OnStartup(e);
    }
    private async Task Listen()
    {
        while(!lifetime.IsCancellationRequested)
        {
            try
            {
                await using var pipe=new NamedPipeServerStream(pipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(lifetime.Token);
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(2));
                byte[] header=new byte[4]; await pipe.ReadExactlyAsync(header,deadline.Token); int length=BinaryPrimitives.ReadInt32LittleEndian(header);
                if(length is <0 or >16000) continue;
                byte[] data=new byte[length]; await pipe.ReadExactlyAsync(data,deadline.Token); string path=Encoding.UTF8.GetString(data);
                await Dispatcher.InvokeAsync(()=> { if(MainWindow is MiniDisplay.Windows.MainWindow window) window.ReceiveLaunch(path); });
            }
            catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
    protected override void OnExit(ExitEventArgs e)
    { lifetime.Cancel(); instance?.Dispose(); base.OnExit(e); }
}
