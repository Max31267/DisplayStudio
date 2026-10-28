using System.ComponentModel;
using System.Diagnostics;
using System.IO.Ports;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MiniDisplay.Core;
using Forms = System.Windows.Forms;

namespace MiniDisplay.Windows;

public partial class MainWindow : Window
{
    private readonly ScreenRenderer renderer = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
    private readonly EditorSurface playbackSurface = new();
    private readonly EditorSurface editorSurface = new();
    private int renderedEditorPixels, renderedPlaybackPixels;
    private readonly Rgb565Surface receiverSurface = new();
    private DisplayPage? renderedLivePage, renderedEditorPage, renderedPlaybackPage;
    private long liveSecond = -1, editorSecond = -1, playbackSecond = -1;
    private Guid? renderedSelection;
    private bool renderedGrid;
    private DisplayProject project = new();
    private RenderedFrame latestFrame = new(Guid.Empty, new byte[FrameDiffer.FrameBytes]);
    private CancellationTokenSource? connectionCancellation;
    private Task? connectionTask;
    private EmulatorLink? emulator;
    private int forceFull = 1;
    private readonly PagePlayback playback = new();
    private readonly Stopwatch playbackClock = Stopwatch.StartNew();
    private long simulatedOutageUntilTicks;
    private bool ready;
    private bool exiting;
    private bool featuresStopped;
    private bool startupFileOpened;

    public MainWindow()
    {
        InitializeComponent();
        InitializeFeatures();
        InitializeSettings();
        InitializeBacklight();
        Preview.Source = playbackSurface.Source; EditorPreview.Source = editorSurface.Source;
        using(var icon=System.Windows.Application.GetResourceStream(new Uri("Assets/App.ico",UriKind.Relative))!.Stream) trayIcon=new System.Drawing.Icon(icon);
        tray = new Forms.NotifyIcon { Text = "Display Studio", Icon = trayIcon, Visible = true };
        tray.DoubleClick += (_, _) => Restore();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => Restore());
        menu.Items.Add("Выход", null, (_, _) => RequestExit());
        tray.ContextMenuStrip = menu;
        InitializeEditor();
        ready = true;
        UpdateWorkspaceNavigation();
        LoadPorts();
        Loaded += (_, _) =>
        {
            EditorPreview.Focus();
            if (startupFileOpened) return; startupFileOpened = true;
            RestoreStartupSession();
        };
        timer.Tick += (_, _) => Render();
        timer.Start();
        Render();
    }

    private void Restore() { Show(); WindowState = WindowState.Normal; Activate(); }

    private void Render()
    {
        renderer.Data = sourceHub.Snapshot; if (WorkspaceTabs.SelectedIndex == 3 && IsVisible) UpdateSourceStatus();
        var animationNow = playbackClock.Elapsed;
        if (!ReferenceEquals(thumbnailSnapshot, renderer.Data) && animationNow - lastThumbnailUpdate >= TimeSpan.FromSeconds(5)) { thumbnailSnapshot = renderer.Data; lastThumbnailUpdate = animationNow; RefreshPagesList(); }
        if (renderer.Data.TrackId != lastTrackId)
        {
            lastTrackId = renderer.Data.TrackId;
            if (lastTrackId.Length > 0 && project.Sources.ShowNewTrack && renderer.Data.Get(DataField.MusicState).Text == "Воспроизводится")
            {
                var musicPage = project.Pages.FirstOrDefault(page => page.Enabled && page.Layers.Any(layer => layer.Visible && (layer.Type == LayerType.Artwork || layer.Type == LayerType.DataText && layer.DataField == DataField.MusicTitle)));
                if (musicPage is not null) playback.Interrupt(project, musicPage.Id, animationNow, TimeSpan.FromSeconds(10));
            }
        }
        UpdateCanvasViewport();
        var current = playback.Update(project, playbackClock.Elapsed);
        foreach (var entry in pageEntries) entry.SetOnAir(entry.Id == current.Id);
        var now = DateTime.Now;
        if (liveAnimationId != current.Id) { liveAnimationId = current.Id; liveAnimationStart = animationNow; }
        if (editorAnimationId != selectedPageId) { editorAnimationId = selectedPageId; editorAnimationStart = animationNow; }
        long second = now.Ticks / TimeSpan.TicksPerSecond;
        if (!ReferenceEquals(current, renderedLivePage) || second != liveSecond || HasAnimation(current))
        {
            var frame = renderer.Render(current, now, elapsed: animationNow - liveAnimationStart);
            Volatile.Write(ref latestFrame, new RenderedFrame(current.Id, frame));
            renderedLivePage = current; liveSecond = second;
        }
        bool showPlayback = WorkspaceTabs.SelectedIndex == 0 && IsVisible && WindowState != WindowState.Minimized;
        int playbackPixels = showPlayback ? ViewportPixelSize(Preview) : renderedPlaybackPixels;
        if (showPlayback && (!ReferenceEquals(current, renderedPlaybackPage) || second != playbackSecond || playbackPixels != renderedPlaybackPixels || HasAnimation(current)))
        {
            playbackSurface.Update(renderer.RenderPlayback(current, now, playbackPixels, animationNow - liveAnimationStart));
            if (!ReferenceEquals(Preview.Source, playbackSurface.Source)) Preview.Source = playbackSurface.Source;
            renderedPlaybackPage = current; playbackSecond = second; renderedPlaybackPixels = playbackPixels;
        }
        PageTitle.Text = current.Name;
        LivePageLabel.Text = "В показе: " + current.Name;
        PlaybackStatus.Text = project.AutoRotate && project.Pages.Count(page => page.Enabled) > 1
            ? $"Следующая страница через {playback.RemainingSeconds(current, playbackClock.Elapsed)} с"
            : project.AutoRotate ? "В показе одна страница" : "Автосмена выключена · показывается одна страница";
        bool grid = GridCheck.IsChecked == true;
        bool showEditor = WorkspaceTabs.SelectedIndex == 1 && IsVisible && WindowState != WindowState.Minimized;
        int pixels = showEditor ? ViewportPixelSize(EditorPreview) : renderedEditorPixels;
        if (showEditor && (!ReferenceEquals(SelectedPage, renderedEditorPage) ||
            second != editorSecond || renderedSelection != selectedLayerId || renderedGrid != grid || pixels != renderedEditorPixels || HasAnimation(SelectedPage)))
        {
            editorSurface.Update(renderer.RenderEditor(SelectedPage, now, pixels, selectedLayerId, grid, animationNow - editorAnimationStart));
            if (!ReferenceEquals(EditorPreview.Source, editorSurface.Source)) EditorPreview.Source = editorSurface.Source;
            renderedEditorPage = SelectedPage; editorSecond = second; renderedSelection = selectedLayerId; renderedGrid = grid;
            renderedEditorPixels = pixels;
        }
    }
    private int ViewportPixelSize(FrameworkElement preview)
    {
        if (!IsLoaded || !preview.IsLoaded || preview.ActualWidth <= 0) return 480;
        var bounds = preview.TransformToAncestor(this).TransformBounds(new Rect(0, 0, preview.ActualWidth, preview.ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(this);
        return Math.Clamp((int)Math.Ceiling(Math.Max(bounds.Width * dpi.DpiScaleX, bounds.Height * dpi.DpiScaleY)), 240, 2048);
    }
    private static bool HasAnimation(DisplayPage page) => page.Layers.Any(layer => layer.Visible && layer.Type is LayerType.Calibration or LayerType.Gif);

    private void LoadPorts()
    {
        string? previous = Ports.SelectedItem as string;
        Ports.Items.Clear();
        Ports.Items.Add("Эмулятор (без платы)");
        try { foreach (string port in SerialPort.GetPortNames().Order()) Ports.Items.Add(port); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetConnectionBadge("Список портов недоступен: " + ex.Message, false, true); }
        Ports.SelectedItem = previous is not null && Ports.Items.Contains(previous) ? previous : Ports.Items[0];
    }

    private void RefreshPorts(object sender, RoutedEventArgs e) => LoadPorts();

    private async void ToggleConnection(object sender, RoutedEventArgs e)
    {
        if (firmwareBusy) return;
        if (connectionCancellation is not null)
        {
            await DisconnectAsync();
            return;
        }
        string selected = Ports.SelectedItem as string ?? "Эмулятор (без платы)";
        bool simulated = selected.StartsWith("Эмулятор", StringComparison.Ordinal);
        emulator = simulated ? new EmulatorLink() : null;
        DevicePreview.Source = null;
        EmulatorBacklightShade.Visibility = Visibility.Collapsed;
        UpdateBacklightState(null);
        ReceiverLabel.Text = simulated ? "Принято эмулятором" : "Результат — на панели устройства";
        connectionCancellation = new CancellationTokenSource();
        Ports.IsEnabled = RefreshPortsButton.IsEnabled = false; ConnectButton.Content = "Отключить";
        CorruptButton.IsEnabled = BreakButton.IsEnabled = simulated;
        var token = connectionCancellation.Token;
        connectionTask = Task.Run(() => RunConnectionAsync(selected, token));
    }
    private async Task DisconnectAsync()
    {
        ConnectButton.IsEnabled=false; connectionCancellation?.Cancel();
        if(connectionTask is not null) await connectionTask;
        connectionCancellation?.Dispose(); connectionCancellation=null; connectionTask=null;
        ConnectButton.Content="Подключить"; ConnectButton.IsEnabled=!firmwareBusy;
        Ports.IsEnabled=RefreshPortsButton.IsEnabled=!firmwareBusy; CorruptButton.IsEnabled=BreakButton.IsEnabled=false;
        SetConnectionBadge("Не подключён",false); Stats.Text="Передача остановлена"; ReceiverLabel.Text="Последнее принятое изображение";
        UpdateBacklightState(null);
    }

    private async Task RunConnectionAsync(string selected, CancellationToken token)
    {
        bool simulated = selected.StartsWith("Эмулятор", StringComparison.Ordinal);
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    IDeviceLink link;
                    if (simulated)
                    {
                        // Reuse the emulated panel RAM across reconnects, just like the LCD.
                        emulator!.Disconnected = DateTime.UtcNow.Ticks < Interlocked.Read(ref simulatedOutageUntilTicks);
                        link = new BorrowedEmulatorLink(emulator);
                    }
                    else link = new SerialDeviceLink(selected);
                    await using var session = new DisplaySession(link);
                    await session.ConnectAsync(token);
                    await Dispatcher.InvokeAsync(() => { SetConnectionBadge(simulated ? "Эмулятор подключён" : selected + " подключён", true); RememberPort(selected); UpdateBacklightState(session.Capabilities!.SupportsBrightness, simulated); });
                    var clock = Stopwatch.StartNew();
                    var lastStats = TimeSpan.Zero;
                    var lastPing = TimeSpan.Zero;
                    var lastSync = TimeSpan.Zero;
                    long bytes = 0; int frames = 0;
                    Guid sentPageId = Guid.Empty;
                    int appliedBrightness = -1;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        if (simulated) emulator!.Disconnected = DateTime.UtcNow.Ticks < Interlocked.Read(ref simulatedOutageUntilTicks);
                        int brightness = Volatile.Read(ref desiredBrightness);
                        if (session.Capabilities!.SupportsBrightness && brightness != appliedBrightness)
                        {
                            await session.SetBrightnessAsync(brightness, token);
                            appliedBrightness = brightness;
                            await Dispatcher.InvokeAsync(() => BacklightApplied(brightness));
                        }
                        var pending = Volatile.Read(ref latestFrame);
                        if (Interlocked.Exchange(ref forceFull, 0) == 1 || pending.PageId != sentPageId) session.RequestFullFrame();
                        bool sent = await session.SendFrameAsync(pending.Pixels, token);
                        if (sent) sentPageId = pending.PageId;
                        if (sent && simulated)
                        {
                            byte[] received = (byte[])emulator!.Device.Pixels.Clone();
                            await Dispatcher.InvokeAsync(() => { receiverSurface.Update(received); DevicePreview.Source = receiverSurface.Source; });
                        }
                        if (clock.Elapsed - lastPing >= TimeSpan.FromSeconds(1))
                        {
                            await session.PingAsync(token); lastPing = clock.Elapsed;
                        }
                        if (clock.Elapsed - lastSync >= TimeSpan.FromMinutes(1))
                        {
                            await session.SyncTimeAsync(token); lastSync = clock.Elapsed;
                        }
                        if (clock.Elapsed - lastStats >= TimeSpan.FromSeconds(1))
                        {
                            double seconds = (clock.Elapsed - lastStats).TotalSeconds;
                            string memory = simulated ? "Эмулятор" : $"Свободно RAM: {session.Capabilities!.FreeHeap / 1024} КиБ";
                            string stats = $"{(session.FramesSent - frames) / seconds:F1} кадр/с  ·  {(session.BytesSent - bytes) / seconds / 1024:F1} КиБ/с  ·  {memory}";
                            await Dispatcher.InvokeAsync(() => Stats.Text = stats);
                            lastStats = clock.Elapsed; bytes = session.BytesSent; frames = session.FramesSent;
                        }
                        await Task.Delay(50, token);
                    }
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
                {
                    await Dispatcher.InvokeAsync(() => { SetConnectionBadge("Восстановление: " + ex.Message, false, true); UpdateBacklightState(null); });
                    await Task.Delay(1000, token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => { SetConnectionBadge("Ошибка соединения: " + ex.Message, false, true); UpdateBacklightState(null); });
        }
    }

    private void SaveProject(object sender, RoutedEventArgs e)
    {
        SaveDocument(false);
    }

    private void OpenProject(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Проект дисплея|*.mdisplay;*.json" };
        if (dialog.ShowDialog(this) != true) return;
        OpenProjectFile(dialog.FileName);
    }
    private void OpenProjectFile(string path, bool quiet = false)
    {
        try
        {
            var loaded = DisplayProject.Load(path);
            foreach (var asset in loaded.Assets) MediaLibrary.Validate(asset);
            if (!ConfirmSaveChanges()) return;
            currentProjectPath = Path.GetFullPath(path); SetProject(loaded);
            RememberProject(currentProjectPath);
            ProjectNameLabel.Text = ProjectDisplayName(path);
            EditorStatus.Text = "Проект открыт";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NotSupportedException) { if(quiet) EditorStatus.Text="Не удалось восстановить последний проект: "+ex.Message; else ShowError("Не удалось открыть проект. " + ex.Message); }
        finally { ready = true; }
    }

    private static void ShowError(string message) => System.Windows.MessageBox.Show(message, "Мини-дисплей", MessageBoxButton.OK, MessageBoxImage.Warning);
    private void CorruptPacket(object sender, RoutedEventArgs e) { if (emulator is not null) emulator.CorruptNextWrite = true; }
    private void BreakConnection(object sender, RoutedEventArgs e) => Interlocked.Exchange(ref simulatedOutageUntilTicks, DateTime.UtcNow.AddSeconds(3).Ticks);
    private void ExitApp(object sender, RoutedEventArgs e) => RequestExit();

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!exiting) { e.Cancel = true; Hide(); return; }
        // Complete asynchronous shutdown before allowing WPF to tear down the dispatcher.
        if (connectionTask is not null && !connectionTask.IsCompleted)
        {
            e.Cancel = true; IsEnabled = false;
            connectionCancellation!.Cancel();
            await connectionTask;
            connectionTask = null; Close(); return;
        }
        if (!featuresStopped)
        {
            e.Cancel = true; IsEnabled = false; featureLifetime.Cancel();
            try { await sourceHub.DisposeAsync(); } finally { featuresStopped = true; Close(); }
            return;
        }
        featureLifetime.Dispose();
        timer.Stop(); renderer.Dispose(); tray.Dispose(); trayIcon.Dispose(); connectionCancellation?.Dispose();
    }

    private sealed class BorrowedEmulatorLink(EmulatorLink owner) : IDeviceLink
    {
        public ValueTask WriteAsync(byte[] data, CancellationToken token) => owner.WriteAsync(data, token);
        public ValueTask<byte[]> ReadAsync(CancellationToken token) => owner.ReadAsync(token);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed record RenderedFrame(Guid PageId, byte[] Pixels);
}
