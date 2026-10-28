using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using MiniDisplay.Core;

namespace MiniDisplay.Windows;

public partial class MainWindow
{
    private readonly SourceHub sourceHub = new();
    private readonly CancellationTokenSource featureLifetime = new();
    private ComboBox dataFieldInput = null!, fitInput = null!, musicInput = null!, quotaInput = null!, cityResults = null!;
    private TextBox cropLeft = null!, cropTop = null!, cropRight = null!, cropBottom = null!, gifFps = null!, gifRepeat = null!, rangeMin = null!, rangeMax = null!, cityQuery = null!, codexPath = null!;
    private StackPanel mediaProperties = null!, animationProperties = null!, dataProperties = null!, rangeProperties = null!;
    private TextBlock pcStatus = null!, weatherStatus = null!, musicStatus = null!, codexStatus = null!, cityLabel = null!, citySearchStatus = null!;
    private CheckBox showTrack = null!;
    private Button replaceMediaButton = null!;
    private SourceSnapshot? displayedSources;
    private Guid liveAnimationId, editorAnimationId;
    private TimeSpan liveAnimationStart, editorAnimationStart;
    private TimeSpan lastThumbnailUpdate;
    private SourceSnapshot? thumbnailSnapshot;
    private string lastTrackId = "";
    private sealed record FieldChoice(DataField Field, string Label) { public override string ToString() => Label; }
    private static readonly FieldChoice[] DataChoices =
    [new(DataField.CpuLoad,"CPU · загрузка"), new(DataField.GpuLoad,"GPU · загрузка"), new(DataField.RamLoad,"RAM · занято, %"), new(DataField.CpuTemperature,"CPU · температура"), new(DataField.GpuTemperature,"GPU · температура"), new(DataField.RamUsed,"RAM · занято, ГиБ"), new(DataField.RamTotal,"RAM · всего, ГиБ"),
     new(DataField.WeatherCity,"Погода · город"),new(DataField.WeatherTemperature,"Погода · температура"),new(DataField.WeatherFeelsLike,"Погода · ощущается"),new(DataField.WeatherHumidity,"Погода · влажность"),new(DataField.WeatherSummary,"Погода · описание"),new(DataField.WeatherSource,"Погода · источник"),
     new(DataField.MusicTitle,"Музыка · трек"),new(DataField.MusicArtist,"Музыка · исполнитель"),new(DataField.MusicAlbum,"Музыка · альбом"),new(DataField.MusicState,"Музыка · состояние"),new(DataField.MusicPosition,"Музыка · позиция"),new(DataField.MusicDuration,"Музыка · длительность"),new(DataField.MusicProgress,"Музыка · прогресс"),
     new(DataField.CodexPrimaryRemaining,"Codex · осталось в основном окне"),new(DataField.CodexSecondaryRemaining,"Codex · осталось во втором окне"),new(DataField.CodexPrimaryReset,"Codex · сброс основного окна"),new(DataField.CodexSecondaryReset,"Codex · сброс второго окна"),new(DataField.CodexPrimaryWindow,"Codex · основное окно"),new(DataField.CodexSecondaryWindow,"Codex · второе окно")];

    private Brush MutedBrush => (Brush)FindResource("Muted");
    private TextBlock Hint(string text) => new() { Text = text, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 5, 0, 10) };
    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 0, 0, 8) };
    private TextBox NumberBox(StackPanel parent, string label)
    {
        parent.Children.Add(Hint(label)); var box = new TextBox { Text = "0", Margin = new Thickness(0, 0, 0, 12) }; box.TextChanged += LayerSettingsChanged; box.LostKeyboardFocus += EndInputGroup; parent.Children.Add(box); return box;
    }
    private Button ActionButton(string text, RoutedEventHandler action)
    { var button = new Button { Content = text, Margin = new Thickness(0, 4, 8, 8), HorizontalAlignment = HorizontalAlignment.Left }; button.Click += action; return button; }
    private void InitializeFeatures()
    {
        mediaProperties = new StackPanel(); mediaProperties.Children.Add(Heading("Изображение"));
        mediaProperties.Children.Add(Hint("Размер можно менять маркерами на холсте. Углы сохраняют пропорции; Shift — свободный размер рамки. Стороны меняют ширину или высоту."));
        fitInput = new ComboBox { ItemsSource = new[] { "Вписать целиком", "Заполнить с обрезкой", "Растянуть" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 12) }; fitInput.SelectionChanged += LayerSettingsChanged; mediaProperties.Children.Add(fitInput);
        replaceMediaButton = ActionButton("Заменить файл…", ImportMedia); replaceMediaButton.Tag = "replace"; mediaProperties.Children.Add(replaceMediaButton);
        mediaProperties.Children.Add(Hint("Обрезка краёв, %"));
        var crop = new UniformGrid { Columns = 2 }; mediaProperties.Children.Add(crop);
        TextBox Crop(string name) { var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) }; crop.Children.Add(panel); return NumberBox(panel, name); }
        cropLeft = Crop("Слева"); cropTop = Crop("Сверху"); cropRight = Crop("Справа"); cropBottom = Crop("Снизу");
        animationProperties = new StackPanel(); animationProperties.Children.Add(Heading("Воспроизведение GIF"));
        gifFps = NumberBox(animationProperties, "Максимум кадров/с · 1–20"); gifRepeat = NumberBox(animationProperties, "Число повторов · 0 — бесконечно");
        animationProperties.Children.Add(Hint("Задержки кадров сохраняются. При ограничении частоты лишние кадры пропускаются."));
        animationProperties.Children.Add(ActionButton("Перезапустить просмотр GIF", (_, _) => { editorAnimationId = Guid.Empty; if (playback.CurrentPageId == selectedPageId) liveAnimationId = Guid.Empty; Render(); }));
        dataProperties = new StackPanel(); dataProperties.Children.Add(Heading("Источник значения"));
        dataFieldInput = new ComboBox { ItemsSource = DataChoices, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 10) }; dataFieldInput.SelectionChanged += LayerSettingsChanged; dataProperties.Children.Add(dataFieldInput);
        dataProperties.Children.Add(Hint("В тексте {value} заменяется текущим значением. Настройки источников находятся в разделе «Данные»."));
        rangeProperties = new StackPanel(); rangeMin = NumberBox(rangeProperties, "Минимум шкалы"); rangeMax = NumberBox(rangeProperties, "Максимум шкалы");
        foreach (var section in new[] { dataProperties, rangeProperties, mediaProperties, animationProperties }) ExtendedLayerProperties.Children.Add(section);
        BuildSourcesScreen();
        sourceHub.SetCodexExecutable(LocalPreferences.Load().CodexExecutable ?? "");
    }
    private void BuildSourcesScreen()
    {
        var root = new StackPanel { MaxWidth = 950, Margin = new Thickness(28), HorizontalAlignment = HorizontalAlignment.Center };
        SourcesScreen.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        root.Children.Add(new TextBlock { Text = "Источники данных", FontSize = 24, FontWeight = FontWeights.SemiBold });
        root.Children.Add(Hint("Погода, компьютер, музыка и Codex обновляются в фоне. Настройки сохраняются в проекте."));
        root.Children.Add(ActionButton("Обновить источники", (_, _) => sourceHub.Refresh()));
        StackPanel Card(string title)
        {
            var panel = new StackPanel(); panel.Children.Add(Heading(title));
            root.Children.Add(new Border { Child = panel, Background = Brushes.White, BorderBrush = (Brush)FindResource("Stroke"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(22), Margin = new Thickness(0, 0, 0, 16) }); return panel;
        }
        Button Template(StackPanel panel, string name, Func<DisplayPage> template) { var button = ActionButton(name, (_, _) => { if (CommitDraft()) InsertPage(template()); }); panel.Children.Add(button); return button; }
        var weather = Card("Погода"); weather.Children.Add(Hint("Обновление раз в 10 минут. Open-Meteo, при сбое — MET Norway. Найдите город и выберите подходящий результат. Знак ≈ у ощущаемой температуры означает расчётную оценку."));
        var weatherCredits = new TextBlock { Margin = new Thickness(0, 0, 0, 10), FontSize = 12 };
        void Credit(string text, string url) { var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(text)) { NavigateUri = new Uri(url) }; link.RequestNavigate += (_, args) => { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); args.Handled = true; }; weatherCredits.Inlines.Add(link); }
        Credit("Open-Meteo", "https://open-meteo.com/"); weatherCredits.Inlines.Add(" · "); Credit("MET Norway", "https://www.met.no/en"); weatherCredits.Inlines.Add(" · "); Credit("CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/"); weather.Children.Add(weatherCredits);
        cityLabel = Hint("Город не выбран"); weather.Children.Add(cityLabel);
        var search = new StackPanel { Orientation = Orientation.Horizontal }; cityQuery = new TextBox { Width = 280, Margin = new Thickness(0, 0, 10, 8) }; search.Children.Add(cityQuery);
        search.Children.Add(ActionButton("Найти город", SearchWeatherCity)); weather.Children.Add(search);
        citySearchStatus = Hint(""); weather.Children.Add(citySearchStatus);
        cityResults = new ComboBox { Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed }; cityResults.SelectionChanged += ChooseWeatherCity; weather.Children.Add(cityResults);
        weatherStatus = Hint("Выберите город"); weather.Children.Add(weatherStatus); Template(weather, "Добавить страницу «Часы и погода»", WidgetTemplates.Weather);
        var pc = Card("Компьютер"); pc.Children.Add(Hint("CPU, GPU и память обновляются раз в секунду. Некоторые температуры могут быть недоступны без дополнительных прав; вместо них показано —.")); pcStatus = Hint("Ожидание данных"); pc.Children.Add(pcStatus); Template(pc, "Добавить страницу компьютера", WidgetTemplates.Computer);
        var music = Card("Музыка · медиасессии Windows"); music.Children.Add(Hint("Выберите приложение или оставьте автоматический выбор Windows. Обложка и прогресс появятся при активной медиасессии."));
        musicInput = new ComboBox { Margin = new Thickness(0, 0, 0, 10) }; musicInput.SelectionChanged += ChangeMusicSource; music.Children.Add(musicInput);
        showTrack = new CheckBox { Content = "Показывать новый трек на 10 секунд", Margin = new Thickness(0, 4, 0, 12) }; showTrack.Checked += ChangeTrackInterruption; showTrack.Unchecked += ChangeTrackInterruption; music.Children.Add(showTrack);
        musicStatus = Hint("Нет активной медиасессии"); music.Children.Add(musicStatus); Template(music, "Добавить музыкальную страницу", WidgetTemplates.Music);
        var codex = Card("Лимиты Codex"); codex.Children.Add(Hint("Обновление раз в минуту через локальный Codex App Server. Используется существующий вход Codex; пароли и токены в проект не копируются."));
        codex.Children.Add(Hint("Путь к codex.exe · пусто — автоматический поиск. Эта настройка хранится только на этом компьютере."));
        codexPath = new TextBox { Margin = new Thickness(0, 0, 0, 8), ToolTip = "Оставьте пустым для автоматического поиска codex.exe" }; codex.Children.Add(codexPath);
        var pathButtons = new WrapPanel(); pathButtons.Children.Add(ActionButton("Найти codex.exe…", ChooseCodexExecutable)); pathButtons.Children.Add(ActionButton("Применить путь", (_, _) => ApplyCodexPath(codexPath.Text.Trim()))); codex.Children.Add(pathButtons);
        codex.Children.Add(Hint("Группа лимитов")); quotaInput = new ComboBox { Margin = new Thickness(0, 0, 0, 10) }; quotaInput.SelectionChanged += ChangeQuota; codex.Children.Add(quotaInput);
        codexStatus = Hint("Ожидание Codex"); codex.Children.Add(codexStatus); Template(codex, "Добавить страницу лимитов Codex", WidgetTemplates.Codex);
    }
    private void FillSourceFields() => Synchronize(() =>
    {
        cityLabel.Text = project.Sources.WeatherCity.Length == 0 ? "Город не выбран" : project.Sources.WeatherCity;
        cityQuery.Text = project.Sources.WeatherCity.Split(',')[0]; codexPath.Text = sourceHub.CodexExecutable; showTrack.IsChecked = project.Sources.ShowNewTrack;
        if (cityResults.SelectedItem is WeatherPlace place && (place.Latitude != project.Sources.Latitude || place.Longitude != project.Sources.Longitude)) cityResults.SelectedIndex = -1;
        if (project.Sources.WeatherCity.Length == 0) citySearchStatus.Text = "";
        displayedSources = null; UpdateSourceStatus();
    });
    private void UpdateSourceStatus()
    {
        var snapshot = sourceHub.Snapshot; if (ReferenceEquals(snapshot, displayedSources)) return; displayedSources = snapshot;
        foreach (var pair in new[] { ("pc", pcStatus), ("weather", weatherStatus), ("music", musicStatus), ("codex", codexStatus) }) if (snapshot.Status.TryGetValue(pair.Item1, out var message)) pair.Item2.Text = message;
        Synchronize(() =>
        {
            var music = new[] { new SourceOption("", "Автоматически · текущая сессия Windows") }.Concat(snapshot.MusicSources).ToList();
            if (!music.Any(item => item.Id == project.Sources.MusicSourceId)) music.Add(new(project.Sources.MusicSourceId, project.Sources.MusicSourceId + " · не активен"));
            if (musicInput.ItemsSource is not SourceOption[] oldMusic || !oldMusic.SequenceEqual(music)) musicInput.ItemsSource = music.ToArray();
            musicInput.SelectedItem = ((SourceOption[])musicInput.ItemsSource).First(item => item.Id == project.Sources.MusicSourceId);
            var groups = snapshot.Quotas.Select(item => new SourceOption(item.Id, item.Name)).ToList(); if (!groups.Any(item => item.Id == project.Sources.CodexLimitId)) groups.Add(new(project.Sources.CodexLimitId, project.Sources.CodexLimitId));
            if (quotaInput.ItemsSource is not SourceOption[] oldGroups || !oldGroups.SequenceEqual(groups)) quotaInput.ItemsSource = groups.ToArray();
            quotaInput.SelectedItem = ((SourceOption[])quotaInput.ItemsSource).First(item => item.Id == project.Sources.CodexLimitId);
        });
    }
    private void ApplySources(SourceSettings settings)
    {
        if (!ready || !CommitDraft()) return;
        history.BreakGroup(); if (!ApplyProject(project with { Sources = settings })) return;
        sourceHub.Refresh(); FillSourceFields();
    }
    private async void SearchWeatherCity(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender; button.IsEnabled = false;
        try { citySearchStatus.Text = "Поиск города…"; var results = await SourceHub.SearchCity(cityQuery.Text, featureLifetime.Token); cityResults.ItemsSource = results; cityResults.Visibility = results.Length == 0 ? Visibility.Collapsed : Visibility.Visible; citySearchStatus.Text = results.Length == 0 ? "Город не найден" : "Выберите город из списка"; }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { citySearchStatus.Text = "Поиск недоступен: " + ex.Message; }
        finally { button.IsEnabled = true; }
    }
    private void ChooseWeatherCity(object sender, SelectionChangedEventArgs e) { if (ready && cityResults.SelectedItem is WeatherPlace place) { ApplySources(project.Sources with { WeatherCity = place.Name, Latitude = place.Latitude, Longitude = place.Longitude }); citySearchStatus.Text = ""; } }
    private void ChooseCodexExecutable(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Filter = "Codex CLI (codex.exe)|codex.exe" }; if (dialog.ShowDialog(this) == true) ApplyCodexPath(dialog.FileName); }
    private void ApplyCodexPath(string path)
    {
        try { if (path.Length > 0) path = Path.GetFullPath(CodexReader.FindExecutable(path)); sourceHub.SetCodexExecutable(path); codexPath.Text = path; LocalPreferences.Update(preferences => preferences with { CodexExecutable = path }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { ShowError("Не удалось применить путь Codex. " + ex.Message); }
    }
    private void ChangeMusicSource(object sender, SelectionChangedEventArgs e) { if (ready && musicInput.SelectedItem is SourceOption option) ApplySources(project.Sources with { MusicSourceId = option.Id }); }
    private void ChangeQuota(object sender, SelectionChangedEventArgs e) { if (ready && quotaInput.SelectedItem is SourceOption option) ApplySources(project.Sources with { CodexLimitId = option.Id }); }
    private void ChangeTrackInterruption(object sender, RoutedEventArgs e) { if (ready) ApplySources(project.Sources with { ShowNewTrack = showTrack.IsChecked == true }); }
    private TextBox[] ExtendedInputs() => [cropLeft, cropTop, cropRight, cropBottom, gifFps, gifRepeat, rangeMin, rangeMax];
    private void FillExtendedLayer(PageLayer layer)
    {
        bool image = layer.Type is LayerType.Image or LayerType.Gif or LayerType.Artwork;
        replaceMediaButton.Visibility = layer.Type == LayerType.Artwork ? Visibility.Collapsed : Visibility.Visible;
        mediaProperties.Visibility = image ? Visibility.Visible : Visibility.Collapsed; animationProperties.Visibility = layer.Type == LayerType.Gif ? Visibility.Visible : Visibility.Collapsed;
        dataProperties.Visibility = layer.Type is LayerType.DataText or LayerType.Bar or LayerType.Ring or LayerType.Graph ? Visibility.Visible : Visibility.Collapsed;
        rangeProperties.Visibility = layer.Type is LayerType.Bar or LayerType.Ring or LayerType.Graph ? Visibility.Visible : Visibility.Collapsed;
        fitInput.SelectedIndex = (int)layer.Fit; cropLeft.Text = layer.CropLeft.ToString(); cropTop.Text = layer.CropTop.ToString(); cropRight.Text = layer.CropRight.ToString(); cropBottom.Text = layer.CropBottom.ToString();
        gifFps.Text = layer.AnimationFps.ToString(); gifRepeat.Text = layer.RepeatCount.ToString(); rangeMin.Text = layer.RangeMin.ToString(CultureInfo.CurrentCulture); rangeMax.Text = layer.RangeMax.ToString(CultureInfo.CurrentCulture);
        dataFieldInput.ItemsSource = DataChoices.Where(item => layer.Type == LayerType.Graph ? item.Field <= DataField.RamTotal : layer.Type is LayerType.Bar or LayerType.Ring ? DataFields.IsNumeric(item.Field) : true).ToArray();
        dataFieldInput.SelectedItem = DataChoices.First(item => item.Field == layer.DataField);
        LayerColorSection.Visibility = image || layer.Type == LayerType.WeatherIcon ? Visibility.Collapsed : Visibility.Visible;
    }
    private bool ReadExtendedLayer(PageLayer layer, out PageLayer updated)
    {
        updated = layer;
        if (layer.Type is LayerType.Image or LayerType.Gif or LayerType.Artwork)
        {
            if (!ReadNumber(cropLeft,"Обрезка слева",0,95,out int left) || !ReadNumber(cropTop,"Обрезка сверху",0,95,out int top) || !ReadNumber(cropRight,"Обрезка справа",0,95,out int right) || !ReadNumber(cropBottom,"Обрезка снизу",0,95,out int bottom)) return false;
            if (left + right > 95 || top + bottom > 95) { cropLeft.Tag = "invalid"; ValidationStatus.Text = "Обрезка должна оставлять не менее 5% изображения."; return false; }
            updated = updated with { CropLeft = left, CropTop = top, CropRight = right, CropBottom = bottom, Fit = (ImageFit)Math.Clamp(fitInput.SelectedIndex,0,2) };
        }
        if (layer.Type == LayerType.Gif)
        {
            if (!ReadNumber(gifFps,"Кадры в секунду",1,20,out int fps) || !ReadNumber(gifRepeat,"Повторы",0,1000,out int repeat)) return false;
            updated = updated with { AnimationFps = fps, RepeatCount = repeat };
        }
        if (layer.Type is LayerType.DataText or LayerType.Bar or LayerType.Ring or LayerType.Graph) updated = updated with { DataField = (dataFieldInput.SelectedItem as FieldChoice)?.Field ?? layer.DataField };
        if (layer.Type is LayerType.Bar or LayerType.Ring or LayerType.Graph)
        {
            double min = 0, max = 0;
            bool valid = double.TryParse(rangeMin.Text, out min) && double.TryParse(rangeMax.Text, out max) && double.IsFinite(min) && double.IsFinite(max) && max > min;
            if (!valid) { rangeMin.Tag = "invalid"; ValidationStatus.Text = "Максимум шкалы должен быть больше минимума. Используйте числа."; return false; }
            rangeMin.Tag = rangeMax.Tag = null; updated = updated with { RangeMin = min, RangeMax = max };
        }
        return true;
    }
    private void ImportMedia(object sender, RoutedEventArgs e)
    {
        if (!CommitDraft()) return;
        bool replace = (sender as FrameworkElement)?.Tag as string == "replace";
        if (replace && SelectedLayer?.Type is not (LayerType.Image or LayerType.Gif)) { ValidationStatus.Text = "Для замены выберите картинку или GIF."; return; }
        var dialog = new OpenFileDialog { Filter = "Изображения и GIF|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|Все файлы|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > ProjectAsset.MaxBytes) throw new InvalidDataException("Файл превышает 8 МиБ.");
            var asset = ProjectAsset.Create(dialog.FileName, File.ReadAllBytes(dialog.FileName)); asset.Validate();
            using var decoded = new MediaImage(asset.Data); decoded.Frame(TimeSpan.Zero, 20, 0);
            var type = decoded.Animated ? LayerType.Gif : LayerType.Image;
            double scale = Math.Min(192d / decoded.Width, 192d / decoded.Height);
            string name = Path.GetFileNameWithoutExtension(asset.Name); if (string.IsNullOrWhiteSpace(name)) name = decoded.Animated ? "GIF" : "Картинка";
            var layer = replace ? SelectedLayer! with { AssetId = asset.Id, Type = type } : new PageLayer { Type = type, Name = UniqueLayerName(name), Text = "", AssetId = asset.Id, Width = Math.Max(1,(int)(decoded.Width * scale)), Height = Math.Max(1,(int)(decoded.Height * scale)) };
            if (!replace) layer = renderer.PlaceNewLayer(SelectedPage, layer, DateTime.Now);
            var page = SelectedPage with { Layers = replace ? SelectedPage.Layers.Select(item => item.Id == layer.Id ? layer : item).ToArray() : [.. SelectedPage.Layers, layer] };
            history.BreakGroup();
            if (!ApplyProject(project with { Assets = project.Assets.Any(item => item.Id == asset.Id) ? project.Assets : [.. project.Assets, asset], Pages = project.Pages.Select(item => item.Id == page.Id ? page : item).ToArray() })) return;
            selectedLayerId = layer.Id; RefreshLayersList(); FillLayerFields(); InspectorTabs.SelectedIndex = 1; EditorStatus.Text = "Ресурс добавлен в проект. Размер, обрезка и повторение настраиваются справа.";
            editorAnimationId = Guid.Empty; if (playback.CurrentPageId == selectedPageId) liveAnimationId = Guid.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { ShowError("Не удалось добавить изображение. " + ex.Message); }
    }
    private void CleanResources(object sender, RoutedEventArgs e)
    {
        if (!CommitDraft()) return; var used = project.Pages.SelectMany(page => page.Layers).Select(layer => layer.AssetId).ToHashSet(); history.BreakGroup();
        ApplyProject(project with { Assets = project.Assets.Where(asset => used.Contains(asset.Id)).ToArray() }); EditorStatus.Text = "Неиспользуемые ресурсы убраны из проекта · Ctrl+Z — отменить";
    }
}
