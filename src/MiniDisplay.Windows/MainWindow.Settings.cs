using System.ComponentModel;
using System.Diagnostics;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MiniDisplay.Core;

namespace MiniDisplay.Windows;

public partial class MainWindow
{
    public void ReceiveLaunch(string path)
    { if(exiting) return; Restore(); if(!firmwareBusy && path.Length>0 && (path.EndsWith(".mdisplay",StringComparison.OrdinalIgnoreCase)||path.EndsWith(".json",StringComparison.OrdinalIgnoreCase))) OpenProjectFile(path); }
    private CheckBox startupInput=null!, restoreInput=null!, autoConnectInput=null!;
    private TextBlock startupStatus=null!, firmwareStatus=null!;
    private ComboBox firmwarePort=null!, firmwareProfile=null!;
    private Button flashButton=null!, flashCancel=null!, flashRefresh=null!;
    private ProgressBar flashProgress=null!;
    private TextBox flashLog=null!;
    private FirmwareBundle? firmwareBundle;
    private CancellationTokenSource? flashCancellation;
    private bool updatingSettings, firmwareBusy;

    private void InitializeSettings()
    {
        var root=new StackPanel { MaxWidth=950,Margin=new Thickness(28),HorizontalAlignment=HorizontalAlignment.Center };
        SettingsScreen.Children.Add(new ScrollViewer { Content=root,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled });
        root.Children.Add(new TextBlock { Text="Настройки",FontSize=24,FontWeight=FontWeights.SemiBold });
        root.Children.Add(Hint("Display Studio 0.4.1 · параметры этого компьютера"));
        StackPanel Card(string title)
        { var panel=new StackPanel(); panel.Children.Add(Heading(title)); root.Children.Add(new Border { Child=panel,Background=Brushes.White,BorderBrush=(Brush)FindResource("Stroke"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(9),Padding=new Thickness(22),Margin=new Thickness(0,10,0,8) }); return panel; }
        var startup=Card("Запуск программы");
        CheckBox Choice(string name,string title,RoutedEventHandler action)
        { var box=new CheckBox { Name=name,Content=title,Margin=new Thickness(0,8,0,8) }; box.Checked+=action; box.Unchecked+=action; startup.Children.Add(box); return box; }
        startupInput=Choice("StartupInput","Запускать Display Studio при входе в Windows",ChangeStartup);
        startup.Children.Add(Hint("Автозапуск работает для текущего пользователя, без прав администратора. Программа запускается в трее. Сначала распакуйте её в постоянную папку."));
        startupStatus=Hint(""); startup.Children.Add(startupStatus);
        restoreInput=Choice("RestoreProjectInput","Открывать последний сохранённый проект",ChangePreferences);
        autoConnectInput=Choice("AutoConnectInput","Подключаться к последнему устройству",ChangePreferences);
        startup.Children.Add(Hint("Крестик сворачивает приложение в трей. Для завершения выберите «Выход из приложения» в меню ⋯ или у значка возле часов."));
        startup.Children.Add(ActionButton("Открыть инструкцию",OpenUserGuide));
        var flash=Card("Прошивка ESP32-C3");
        flash.Children.Add(Hint("Подключите ESP32-C3 SuperMini по USB-кабелю с передачей данных. Для первой прошивки зажмите BOOT, нажмите RESET и отпустите BOOT."));
        flash.Children.Add(Hint("COM-порт платы"));
        var ports=new DockPanel(); flashRefresh=ActionButton("Обновить",(_,_)=>RefreshFirmwarePorts()); DockPanel.SetDock(flashRefresh,Dock.Right); ports.Children.Add(flashRefresh);
        firmwarePort=new ComboBox { MinWidth=180,Margin=new Thickness(0,4,12,8) }; firmwarePort.SelectionChanged+=(_,_)=>UpdateFlashAvailability(); ports.Children.Add(firmwarePort); flash.Children.Add(ports);
        flash.Children.Add(Hint("Вариант дисплея ST7789 240×240")); firmwareProfile=new ComboBox { Margin=new Thickness(0,0,0,12) }; flash.Children.Add(firmwareProfile);
        var actions=new WrapPanel(); flashButton=ActionButton("Прошить ESP32-C3",StartFirmwareFlash); flashButton.Style=(Style)FindResource("PrimaryButton"); actions.Children.Add(flashButton);
        flashCancel=ActionButton("Прервать",CancelFirmwareFlash); flashCancel.IsEnabled=false; actions.Children.Add(flashCancel); flash.Children.Add(actions);
        flashProgress=new ProgressBar { Minimum=0,Maximum=100,Height=8,Margin=new Thickness(0,6,0,8) }; flash.Children.Add(flashProgress);
        firmwareStatus=Hint(""); flash.Children.Add(firmwareStatus);
        flashLog=new TextBox { IsReadOnly=true,AcceptsReturn=true,FontFamily=new FontFamily("Consolas"),FontSize=11,MinHeight=150,MaxHeight=230,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,TextWrapping=TextWrapping.Wrap };
        flash.Children.Add(new Expander { Header="Журнал прошивки",Content=flashLog,Margin=new Thickness(0,6,0,0) });
        flash.Children.Add(Hint("Программа сама освободит COM-порт. Прошивка предназначена только для ESP32-C3 с флеш-памятью 4 МиБ. На реальной плате этот комплект ещё не проверялся."));
        updatingSettings=true;
        try { var preferences=LocalPreferences.Load(); restoreInput.IsChecked=preferences.RestoreProject; autoConnectInput.IsChecked=preferences.AutoConnect; RefreshStartupState(); }
        finally { updatingSettings=false; }
        try { firmwareBundle=FirmwareBundle.Load(AppContext.BaseDirectory); firmwareProfile.ItemsSource=firmwareBundle.Profiles; firmwareProfile.SelectedIndex=0; firmwareStatus.Text="Готово к прошивке · комплект "+firmwareBundle.Version; }
        catch(Exception ex) when(ex is IOException or InvalidDataException or System.Text.Json.JsonException) { firmwareStatus.Text=ex.Message; }
        RefreshFirmwarePorts();
    }
    private void RefreshStartupState()
    {
        try
        {
            string executable=Environment.ProcessPath ?? "";
            bool published=Path.GetFileName(executable).Equals("MiniDisplay.exe",StringComparison.OrdinalIgnoreCase);
            string registered=StartupRegistration.Read(); startupInput.IsEnabled=published;
            startupInput.IsChecked=published && registered.Equals(StartupRegistration.Command(executable),StringComparison.OrdinalIgnoreCase);
            startupStatus.Text=!published?"Автозагрузка доступна в готовой сборке программы.":registered.Length==0?"Автозагрузка выключена.":startupInput.IsChecked==true?"Автозагрузка включена для этой копии программы.":"В автозагрузке указана другая копия. Включите галочку, чтобы использовать эту.";
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { startupInput.IsEnabled=false; startupStatus.Text="Автозагрузка недоступна: "+ex.Message; }
    }
    private void ChangeStartup(object sender,RoutedEventArgs e)
    {
        if(updatingSettings || !ready) return;
        updatingSettings=true;
        try { StartupRegistration.Set(startupInput.IsChecked==true); RefreshStartupState(); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { ShowError("Не удалось изменить автозагрузку. "+ex.Message); RefreshStartupState(); }
        finally { updatingSettings=false; }
    }
    private void ChangePreferences(object sender,RoutedEventArgs e)
    {
        if(updatingSettings || !ready) return;
        try { LocalPreferences.Update(preferences=>preferences with { RestoreProject=restoreInput.IsChecked==true,AutoConnect=autoConnectInput.IsChecked==true }); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { ShowError("Не удалось сохранить настройки. "+ex.Message); updatingSettings=true; try { var saved=LocalPreferences.Load(); restoreInput.IsChecked=saved.RestoreProject; autoConnectInput.IsChecked=saved.AutoConnect; } finally { updatingSettings=false; } }
    }
    private void RememberProject(string path)
    { try { LocalPreferences.Update(preferences=>preferences with { LastProjectPath=path,LastPageId=playback.CurrentPageId }); } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { } }
    private static void RememberPort(string port)
    { try { LocalPreferences.Update(preferences=>preferences with { LastPort=port.StartsWith("Эмулятор",StringComparison.Ordinal)?"emulator":port }); } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { } }
    private void RestoreStartupSession()
    {
        var arguments=Environment.GetCommandLineArgs().Skip(1).ToArray(); var preferences=LocalPreferences.Load();
        string? requested=arguments.FirstOrDefault(item=>item.EndsWith(".mdisplay",StringComparison.OrdinalIgnoreCase)||item.EndsWith(".json",StringComparison.OrdinalIgnoreCase));
        if(requested is not null) OpenProjectFile(requested);
        else if(preferences.RestoreProject && !string.IsNullOrWhiteSpace(preferences.LastProjectPath) && File.Exists(preferences.LastProjectPath))
        {
            OpenProjectFile(preferences.LastProjectPath,true);
            if(currentProjectPath==preferences.LastProjectPath && project.Pages.Any(page=>page.Id==preferences.LastPageId && page.Enabled))
            { playback.Select(preferences.LastPageId,playbackClock.Elapsed); liveAnimationId=Guid.Empty; RememberProject(currentProjectPath); }
        }
        if(arguments.Contains("--autostart",StringComparer.OrdinalIgnoreCase)) Hide();
        if(preferences.AutoConnect && !string.IsNullOrWhiteSpace(preferences.LastPort))
        {
            string? port=preferences.LastPort=="emulator"?Ports.Items[0] as string:FirmwareBundle.ValidPort(preferences.LastPort)?preferences.LastPort:null;
            if(port is not null) { if(!Ports.Items.Contains(port)) Ports.Items.Add(port); Ports.SelectedItem=port; ToggleConnection(this,new RoutedEventArgs()); }
        }
    }
    private void OpenUserGuide(object sender,RoutedEventArgs e)
    {
        string path=Path.Combine(AppContext.BaseDirectory,"Docs","README.html");
        try { if(!File.Exists(path)) throw new FileNotFoundException("Инструкция не найдена. Распакуйте полный комплект программы."); Process.Start(new ProcessStartInfo(path) { UseShellExecute=true }); }
        catch(Exception ex) when(ex is IOException or Win32Exception) { ShowError(ex.Message); }
    }
    private void RefreshFirmwarePorts()
    {
        string? previous=firmwarePort.SelectedItem as string;
        try { firmwarePort.ItemsSource=SerialPort.GetPortNames().Where(FirmwareBundle.ValidPort).Order().ToArray(); firmwarePort.SelectedItem=previous; if(firmwarePort.SelectedIndex<0 && firmwarePort.Items.Count>0) firmwarePort.SelectedIndex=0; }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { firmwareStatus.Text="Не удалось прочитать список портов: "+ex.Message; }
        UpdateFlashAvailability();
    }
    private void UpdateFlashAvailability()
    { if(flashButton is not null) flashButton.IsEnabled=!firmwareBusy && firmwareBundle is not null && firmwarePort.SelectedItem is string; }
    private async void StartFirmwareFlash(object sender,RoutedEventArgs e)
    {
        if(firmwareBusy || firmwareBundle is null || firmwareProfile.SelectedItem is not FirmwareProfile profile || firmwarePort.SelectedItem is not string port) return;
        if(System.Windows.MessageBox.Show(this,$"Прошить ESP32-C3 на {port}?\nВариант: {profile.Name}\n\nТекущая прошивка и настройки платы в записываемой области будут заменены. Не отключайте USB до завершения.","Прошивка дисплея",MessageBoxButton.OKCancel,MessageBoxImage.Warning)!=MessageBoxResult.OK) return;
        flashLog.Clear(); flashProgress.Value=0; flashProgress.IsIndeterminate=true; flashCancellation=new(); firmwareBusy=true;
        firmwarePort.IsEnabled=firmwareProfile.IsEnabled=flashRefresh.IsEnabled=flashButton.IsEnabled=false; flashCancel.IsEnabled=true; ConnectButton.IsEnabled=false;
        try
        {
            if(connectionCancellation is not null) await DisconnectAsync();
            ConnectButton.IsEnabled=Ports.IsEnabled=RefreshPortsButton.IsEnabled=false;
            firmwareStatus.Text="Проверяем комплект и прошиваем "+port+"…";
            var command=await Task.Run(()=>firmwareBundle.Prepare(AppContext.BaseDirectory,profile.Id,port));
            var progress=new Progress<string>(line=>
            {
                if(flashLog.Text.Length>40000) flashLog.Text=flashLog.Text[^20000..];
                flashLog.AppendText(line+Environment.NewLine); flashLog.ScrollToEnd();
                var percent=System.Text.RegularExpressions.Regex.Match(line,@"(\d+(?:\.\d+)?)\s*%");
                if(percent.Success && double.TryParse(percent.Groups[1].Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double value)) { flashProgress.IsIndeterminate=false; flashProgress.Value=Math.Clamp(value,0,100); }
            });
            await FirmwareFlasher.Run(command,progress,flashCancellation.Token);
            flashProgress.Value=100; firmwareStatus.Text="Прошивка завершена. Если порт пропал, нажмите RESET. Откройте «Устройство», выберите COM-порт и подключитесь.";
            LoadPorts(); if(Ports.Items.Contains(port)) Ports.SelectedItem=port;
        }
        catch(OperationCanceledException) { firmwareStatus.Text="Прошивка прервана. Перед использованием платы прошейте её заново."; }
        catch(Exception ex) when(ex is IOException or InvalidDataException or Win32Exception or TimeoutException or UnauthorizedAccessException) { firmwareStatus.Text=ex.Message; flashLog.AppendText(Environment.NewLine+ex.Message); }
        finally
        {
            flashProgress.IsIndeterminate=false; flashCancellation.Dispose(); flashCancellation=null; firmwareBusy=false;
            firmwarePort.IsEnabled=firmwareProfile.IsEnabled=flashRefresh.IsEnabled=true; flashCancel.IsEnabled=false;
            ConnectButton.IsEnabled=Ports.IsEnabled=RefreshPortsButton.IsEnabled=true; UpdateFlashAvailability();
        }
    }
    private void CancelFirmwareFlash(object sender,RoutedEventArgs e)
    { if(flashCancellation is not null && System.Windows.MessageBox.Show(this,"Прервать прошивку? После этого потребуется прошить плату заново.","Прерывание прошивки",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes) flashCancellation.Cancel(); }
}
