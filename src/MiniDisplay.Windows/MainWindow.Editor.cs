using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiniDisplay.Core;

namespace MiniDisplay.Windows;

public partial class MainWindow
{
    private Guid selectedPageId;
    private Guid? selectedLayerId;
    private Point? dragOrigin;
    private PageLayer? canvasDragStart;
    private ResizeHandle resizeHandle;
    private string? editGroup;
    private ProjectHistory history = null!;
    private DisplayProject savedSnapshot = null!;
    private readonly ObservableCollection<EditorEntry> pageEntries = [];
    private readonly ObservableCollection<EditorEntry> layerEntries = [];
    private readonly ObservableCollection<string> fontFamilies = ["Segoe UI", "Arial", "Calibri", "Consolas", "Verdana", "Tahoma", "Georgia", "Times New Roman"];
    private DisplayPage SelectedPage => project.Pages.First(page => page.Id == selectedPageId);
    private PageLayer? SelectedLayer => SelectedPage.Layers.FirstOrDefault(layer => layer.Id == selectedLayerId);
    private sealed record LayerChoice(LayerType Type, string Label)
    {
        public override string ToString() => Label;
    }

    public sealed class EditorEntry(Guid id, string name, string summary) : INotifyPropertyChanged
    {
        public Guid Id { get; } = id;
        public string Name { get; private set; } = name;
        public string Summary { get; private set; } = summary;
        public string Caption => Name + " · " + Summary;
        public ImageSource? Thumbnail { get; private set; }
        public string Glyph { get; private set; } = "T";
        public bool IsVisible { get; private set; } = true;
        public bool IsOnAir { get; private set; }
        public double Opacity => IsVisible ? 1 : 0.5;
        internal string? ThumbnailBackground;
        internal PageLayer[]? ThumbnailLayers;
        internal SourceSnapshot? ThumbnailData;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(string name, string summary)
        {
            if (Name == name && Summary == summary) return;
            Name = name; Summary = summary; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
        public void SetAppearance(string glyph, bool visible)
        {
            if (Glyph == glyph && IsVisible == visible) return;
            Glyph = glyph; IsVisible = visible; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
        public void SetThumbnail(ImageSource image) { Thumbnail = image; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
        public void SetOnAir(bool value) { if (IsOnAir == value) return; IsOnAir = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOnAir))); }
    }

    private void InitializeEditor()
    {
        history = new ProjectHistory(project);
        FontFamilyInput.ItemsSource = fontFamilies;
        PagesList.ItemsSource = pageEntries; LayersList.ItemsSource = layerEntries;
        LayerTemplate.ItemsSource = new[]
        {
            new LayerChoice(LayerType.Text, "Текст"), new(LayerType.Clock, "Часы HH:mm"),
            new(LayerType.Seconds, "Секунды"), new(LayerType.Weekday, "День недели"),
            new(LayerType.Date, "Дата"), new(LayerType.SecondsBar, "Шкала секунд"), new(LayerType.Calibration, "Тест цветов"),
            new(LayerType.DataText,"Значение из источника"), new(LayerType.Bar,"Шкала"), new(LayerType.Ring,"Кольцо"), new(LayerType.Graph,"График"), new(LayerType.Artwork,"Обложка музыки"), new(LayerType.WeatherIcon,"Погодная иконка")
        };
        LayerTemplate.SelectedIndex = 0; SetProject(project);
    }
    private void Synchronize(Action action)
    {
        bool previous = ready; ready = false;
        try { action(); } finally { ready = previous; }
    }
    private void SetProject(DisplayProject loaded)
    {
        loaded.Validate(); project = loaded; savedSnapshot = loaded; history.Reset(loaded);
        liveAnimationId = editorAnimationId = Guid.Empty;
        renderer.Assets = loaded.Assets; sourceHub.SetProject(loaded); FillSourceFields();
        selectedPageId = project.Pages[0].Id; selectedLayerId = null;
        playback.Select(project.Pages.First(page => page.Enabled).Id, playbackClock.Elapsed);
        Synchronize(() => { RotateCheck.IsChecked = loaded.AutoRotate; RefreshPagesList(); FillPageFields(); });
        timer.Interval = TimeSpan.FromSeconds(1d / loaded.FramesPerSecond);
        InspectorTabs.SelectedIndex = 0; ValidationStatus.Text = ""; Interlocked.Exchange(ref forceFull, 1); UpdateDocumentState();
    }
    private static void SyncEntries(ObservableCollection<EditorEntry> entries, (Guid Id, string Name, string Summary)[] desired)
    {
        for (int i = 0; i < desired.Length; i++)
        {
            var value = desired[i];
            var existing = entries.FirstOrDefault(entry => entry.Id == value.Id);
            if (existing is null) entries.Insert(i, new EditorEntry(value.Id, value.Name, value.Summary));
            else { int from = entries.IndexOf(existing); if (from != i) entries.Move(from, i); existing.Update(value.Name, value.Summary); }
        }
        while (entries.Count > desired.Length) entries.RemoveAt(entries.Count - 1);
    }
    private void RefreshPagesList() => Synchronize(() =>
    {
        SyncEntries(pageEntries, project.Pages.Select(page => (page.Id, page.Name, page.Summary)).ToArray());
        foreach (var page in project.Pages)
        {
            var entry = pageEntries.First(item => item.Id == page.Id);
            entry.SetAppearance("", page.Enabled);
            entry.SetOnAir(page.Id == playback.CurrentPageId);
            bool liveData = page.Layers.Any(layer => layer.Type is LayerType.DataText or LayerType.Bar or LayerType.Ring or LayerType.Graph or LayerType.Artwork or LayerType.WeatherIcon);
            if (dragOrigin is null && (entry.ThumbnailBackground != page.Background || !ReferenceEquals(entry.ThumbnailLayers, page.Layers) || liveData && !ReferenceEquals(entry.ThumbnailData, renderer.Data)))
            {
                var bitmap = renderer.RenderEditor(page, DateTime.Now, 240, thumbnail: true);
                var image = new WriteableBitmap(240, 240, 96, 96, PixelFormats.Bgra32, null);
                image.WritePixels(new Int32Rect(0, 0, 240, 240), bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
                image.Freeze(); entry.SetThumbnail(image);
                entry.ThumbnailBackground = page.Background; entry.ThumbnailLayers = page.Layers; entry.ThumbnailData = renderer.Data;
            }
        }
        PagesList.SelectedItem = pageEntries.FirstOrDefault(entry => entry.Id == selectedPageId);
        PageCountLabel.Text = project.Pages.Length.ToString();
    });
    private void RefreshLayersList() => Synchronize(() =>
    {
        SyncEntries(layerEntries, SelectedPage.Layers.Select(layer => (layer.Id, layer.Name, layer.Visible ? "" : "Скрыт")).ToArray());
        foreach (var layer in SelectedPage.Layers)
            layerEntries.First(entry => entry.Id == layer.Id).SetAppearance(layer.Type switch
            {
                LayerType.Clock => "◷", LayerType.Seconds => "ss", LayerType.Date or LayerType.Weekday => "▦",
                LayerType.SecondsBar or LayerType.Bar => "▥", LayerType.Calibration => "◈", LayerType.Image or LayerType.Gif => "▧", LayerType.Artwork => "♫", LayerType.Ring => "◔", LayerType.Graph => "⌁", LayerType.WeatherIcon => "☀", LayerType.DataText => "#", _ => "T"
            }, layer.Visible);
        LayersList.SelectedItem = layerEntries.FirstOrDefault(entry => entry.Id == selectedLayerId);
        LayerCountLabel.Text = SelectedPage.Layers.Length.ToString();
    });
    private void FillPageFields() => Synchronize(() =>
    {
        var selected = SelectedPage;
        PageNameInput.Text = selected.Name; DurationInput.Text = selected.DurationSeconds.ToString();
        PageNameInput.Tag = DurationInput.Tag = null;
        PageEnabled.IsChecked = selected.Enabled; BackgroundPicker.ColorHex = selected.Background;
        EditingPageTitle.Text = selected.Name; RefreshLayersList(); FillLayerFields();
    });
    private void FillLayerFields() => Synchronize(() =>
    {
        var layer = SelectedLayer;
        LayerProperties.Visibility = layer is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyLayerHint.Visibility = layer is null ? Visibility.Visible : Visibility.Collapsed;
        foreach (var input in LayerInputs()) input.Tag = null;
        if (layer is null) return;
        LayerNameInput.Text = layer.Name; LayerTextInput.Text = layer.Text;
        if (!fontFamilies.Contains(layer.FontFamily)) fontFamilies.Add(layer.FontFamily);
        FontFamilyInput.SelectedItem = layer.FontFamily;
        LayerTextSection.Visibility = layer.Type is LayerType.Text or LayerType.DataText ? Visibility.Visible : Visibility.Collapsed;
        XInput.Text = layer.X.ToString(); YInput.Text = layer.Y.ToString(); FontInput.Text = layer.FontSize.ToString();
        WidthInput.Text = layer.Width.ToString(); HeightInput.Text = layer.Height.ToString();
        bool shape = layer.Type is not (LayerType.Text or LayerType.Clock or LayerType.Seconds or LayerType.Weekday or LayerType.Date);
        ShapeProperties.Visibility = shape ? Visibility.Visible : Visibility.Collapsed;
        TextProperties.Visibility = shape && layer.Type != LayerType.DataText ? Visibility.Collapsed : Visibility.Visible;
        LayerTypeLabel.Text = layer.Type switch
        {
            LayerType.Text => "ТЕКСТ", LayerType.Clock => "ТЕКУЩЕЕ ВРЕМЯ", LayerType.Seconds => "СЕКУНДЫ",
            LayerType.Weekday => "ДЕНЬ НЕДЕЛИ", LayerType.Date => "ДАТА", LayerType.SecondsBar => "ШКАЛА СЕКУНД", LayerType.Calibration => "ТЕСТ ЦВЕТОВ",
            LayerType.Image => "КАРТИНКА", LayerType.Gif => "GIF", LayerType.DataText => "ЗНАЧЕНИЕ ИЗ ИСТОЧНИКА", LayerType.Bar => "ШКАЛА", LayerType.Ring => "КОЛЬЦО", LayerType.Graph => "ГРАФИК", LayerType.Artwork => "ОБЛОЖКА МУЗЫКИ", _ => "ПОГОДНАЯ ИКОНКА"
        };
        LayerColorPicker.ColorHex = layer.Color; LayerBold.IsChecked = layer.Bold; LayerVisible.IsChecked = layer.Visible;
        FillExtendedLayer(layer);
    });
    private TextBox[] LayerInputs() => [LayerNameInput, LayerTextInput, XInput, YInput, FontInput, WidthInput, HeightInput, .. ExtendedInputs()];
    private bool HasInvalidInputs() => PageNameInput.Tag as string == "invalid" || DurationInput.Tag as string == "invalid" || LayerInputs().Any(input => input.Tag as string == "invalid");
    private void UpdateDocumentState()
    {
        if (savedSnapshot is null) return;
        bool dirty = !ProjectHistory.Equivalent(project, savedSnapshot) || HasInvalidInputs();
        DirtyMark.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        ProjectStateLabel.Text = dirty ? "Есть изменения" : currentProjectPath is null ? "Новый проект" : "Сохранено";
        UndoButton.IsEnabled = history.CanUndo; RedoButton.IsEnabled = history.CanRedo;
    }
    private bool ApplyProject(DisplayProject candidate)
    {
        try { history.Apply(candidate, editGroup); }
        catch (InvalidDataException ex) { ValidationStatus.Text = ex.Message; return false; }
        project = history.Current;
        renderer.Assets = project.Assets; sourceHub.SetProject(project);
        if (!project.Pages.Any(page => page.Id == selectedPageId)) selectedPageId = project.Pages[0].Id;
        if (selectedLayerId is Guid id && !SelectedPage.Layers.Any(layer => layer.Id == id)) selectedLayerId = null;
        ValidationStatus.Text = ""; RefreshPagesList(); RefreshLayersList(); UpdateDocumentState();
        return true;
    }
    private bool ApplyPage(DisplayPage updated) => ApplyProject(project with
    {
        Pages = project.Pages.Select(page => page.Id == updated.Id ? updated : page).ToArray()
    });
    private void PageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || PagesList.SelectedItem is not EditorEntry entry || entry.Id == selectedPageId) return;
        if (!CommitDraft()) { RefreshPagesList(); return; }
        history.BreakGroup(); selectedPageId = entry.Id; selectedLayerId = null;
        RefreshPagesList(); FillPageFields(); ValidationStatus.Text = "";
        InspectorTabs.SelectedIndex = 0;
        if (WorkspaceTabs.SelectedIndex == 0) StartSelectedPage();
        else EditorStatus.Text = "Выберите элемент на холсте или нажмите «Добавить элемент»";
    }
    private void PageClicked(object sender, MouseButtonEventArgs e)
    {
        // SelectionChanged does not fire when an already selected item is clicked after auto-advance.
        bool click = reorderOrigin.HasValue && ReferenceEquals(reorderList, PagesList);
        reorderOrigin = null;
        if (!ready || !click ||
            ItemsControl.ContainerFromElement(PagesList, e.OriginalSource as DependencyObject) is not ListBoxItem { DataContext: EditorEntry entry } ||
            entry.Id != selectedPageId) return;
        if (WorkspaceTabs.SelectedIndex == 1) { if (selectedLayerId.HasValue) ShowPageSettings(sender,e); return; }
        if (WorkspaceTabs.SelectedIndex != 0 || playback.CurrentPageId == entry.Id || !CommitDraft()) return;
        StartSelectedPage();
    }
    private bool ReadNumber(TextBox input, string label, int min, int max, out int value)
    {
        bool valid = int.TryParse(input.Text, out value) && value >= min && value <= max;
        input.Tag = valid ? null : "invalid";
        if (!valid) ValidationStatus.Text = $"{label}: введите целое число от {min} до {max}.";
        return valid;
    }
    private bool CommitPageSettings()
    {
        if (string.IsNullOrWhiteSpace(PageNameInput.Text)) { PageNameInput.Tag = "invalid"; ValidationStatus.Text = "Введите название страницы."; return false; }
        PageNameInput.Tag = null;
        if (!ReadNumber(DurationInput, "Время показа", 1, 3600, out int duration)) return false;
        bool result = ApplyPage(SelectedPage with { Name = PageNameInput.Text, DurationSeconds = duration, Enabled = PageEnabled.IsChecked == true, Background = BackgroundPicker.ColorHex });
        if (!result && project.Pages.Count(page => page.Enabled) == 1 && SelectedPage.Enabled) Synchronize(() => PageEnabled.IsChecked = true);
        if (result) EditingPageTitle.Text = SelectedPage.Name;
        return result;
    }
    private bool CommitLayerSettings()
    {
        if (SelectedLayer is not { } layer) return true;
        if (string.IsNullOrWhiteSpace(LayerNameInput.Text)) { LayerNameInput.Tag = "invalid"; ValidationStatus.Text = "Введите название слоя."; return false; }
        LayerNameInput.Tag = null;
        if (!ReadNumber(XInput, "Положение X", 0, 239, out int x) || !ReadNumber(YInput, "Положение Y", 0, 239, out int y) ||
            !ReadNumber(FontInput, "Размер шрифта", 6, 96, out int font) || !ReadNumber(WidthInput, "Ширина", 1, 240, out int width) || !ReadNumber(HeightInput, "Высота", 1, 240, out int height)) return false;
        var updated = layer with { Name = LayerNameInput.Text, Text = LayerTextInput.Text, X = x, Y = y, FontSize = font, FontFamily = FontFamilyInput.SelectedItem as string ?? layer.FontFamily, Width = width, Height = height, Color = LayerColorPicker.ColorHex, Bold = LayerBold.IsChecked == true, Visible = LayerVisible.IsChecked == true };
        if (!ReadExtendedLayer(updated, out updated)) return false;
        if (layer.Type == LayerType.Gif && (updated.AnimationFps != layer.AnimationFps || updated.RepeatCount != layer.RepeatCount)) { editorAnimationId = Guid.Empty; if (playback.CurrentPageId == selectedPageId) liveAnimationId = Guid.Empty; }
        if (updated == layer) { ValidationStatus.Text = ""; return true; }
        return ApplyPage(SelectedPage with { Layers = SelectedPage.Layers.Select(item => item.Id == layer.Id ? updated : item).ToArray() });
    }
    private bool CommitDraft()
    {
        bool result = CommitPageSettings() && CommitLayerSettings(); UpdateDocumentState(); return result;
    }
    private void PageSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        editGroup = "page:" + selectedPageId + ":" + (sender as FrameworkElement)?.Name;
        CommitPageSettings(); editGroup = null; UpdateDocumentState();
    }
    private void LayerSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        editGroup = "layer:" + selectedLayerId + ":" + (sender as FrameworkElement)?.Name;
        CommitLayerSettings(); editGroup = null; UpdateDocumentState();
    }
    private void EndInputGroup(object sender, KeyboardFocusChangedEventArgs e) { if (ready) history.BreakGroup(); }
    private void RotationChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        history.BreakGroup(); ApplyProject(project with { AutoRotate = RotateCheck.IsChecked == true });
        playback.Select(playback.CurrentPageId, playbackClock.Elapsed);
    }
    private void ShowSelectedPage(object sender, RoutedEventArgs e)
    {
        if (!CommitDraft()) return;
        if (!StartSelectedPage()) return;
        WorkspaceTabs.SelectedIndex = 0;
    }
    private bool StartSelectedPage()
    {
        if (!SelectedPage.Enabled) { ValidationStatus.Text = "Включите страницу, чтобы показать её."; return false; }
        playback.Select(selectedPageId, playbackClock.Elapsed); liveAnimationId = Guid.Empty; Interlocked.Exchange(ref forceFull, 1);
        if(currentProjectPath is not null) RememberProject(currentProjectPath);
        EditorStatus.Text = "Показывается: " + SelectedPage.Name; Render(); return true;
    }
    private void InsertPage(DisplayPage added)
    {
        if (project.Pages.Length >= 64) { ValidationStatus.Text = "В проекте уже 64 страницы."; return; }
        var pages = project.Pages.ToList(); pages.Insert(Array.FindIndex(project.Pages, page => page.Id == selectedPageId) + 1, added);
        history.BreakGroup(); if (!ApplyProject(project with { Pages = pages.ToArray() })) return;
        selectedPageId = added.Id; selectedLayerId = null; RefreshPagesList(); FillPageFields(); InspectorTabs.SelectedIndex = 0;
        PagesList.ScrollIntoView(PagesList.SelectedItem);
        WorkspaceTabs.SelectedIndex = 1;
    }
    private void AddPage(object sender, RoutedEventArgs e)
    {
        if (!CommitDraft()) return;
        InsertPage(PageTemplate.SelectedIndex switch { 0 => PageTemplates.Clock(), 1 => PageTemplates.Text(), 2 => PageTemplates.Calibration(), _ => new DisplayPage() });
    }
    private void DuplicatePage(object sender, RoutedEventArgs e) { if (CommitDraft()) InsertPage(SelectedPage.Duplicate()); }
    private void DeletePage(object sender, RoutedEventArgs e)
    {
        if (!CommitDraft()) return;
        int index = Array.FindIndex(project.Pages, page => page.Id == selectedPageId);
        var pages = project.Pages.Where(page => page.Id != selectedPageId).ToArray();
        history.BreakGroup(); if (!ApplyProject(project with { Pages = pages })) return;
        selectedPageId = pages[Math.Min(index, pages.Length - 1)].Id; selectedLayerId = null;
        RefreshPagesList(); FillPageFields(); EditorStatus.Text = "Страница удалена · Ctrl+Z — вернуть";
    }
    private void MovePage(int delta)
    {
        if (!CommitDraft()) return;
        var pages = project.Pages.ToList(); int from = pages.FindIndex(page => page.Id == selectedPageId), to = from + delta;
        if (to < 0 || to >= pages.Count) return;
        history.BreakGroup(); (pages[from], pages[to]) = (pages[to], pages[from]); ApplyProject(project with { Pages = pages.ToArray() });
    }
    private void MovePageUp(object sender, RoutedEventArgs e) => MovePage(-1);
    private void MovePageDown(object sender, RoutedEventArgs e) => MovePage(1);
    private void LayerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        Guid? id = (LayersList.SelectedItem as EditorEntry)?.Id;
        if (id == selectedLayerId) return;
        if (!CommitDraft()) { RefreshLayersList(); return; }
        history.BreakGroup(); selectedLayerId = id; RefreshLayersList(); FillLayerFields();
        InspectorTabs.SelectedIndex = id is null ? 0 : 1;
    }
    private void AddLayer(object sender, RoutedEventArgs e)
    {
        if (!CommitDraft()) return;
        if (SelectedPage.Layers.Length >= 64) { ValidationStatus.Text = "На странице уже 64 слоёв."; return; }
        var choice = (LayerChoice)LayerTemplate.SelectedItem;
        var layer = new PageLayer { Type = choice.Type, Name = UniqueLayerName(choice.Label), Text = choice.Type == LayerType.Text ? "Новый текст" : "" };
        if (choice.Type == LayerType.DataText) layer = layer with { Text = "{value}", Height = 30 };
        if (choice.Type is LayerType.Artwork or LayerType.WeatherIcon or LayerType.Ring) layer = layer with { Width = 100, Height = 100 };
        if (choice.Type == LayerType.Graph) layer = layer with { Width = 200, Height = 60 };
        if (choice.Type == LayerType.Bar) layer = layer with { Width = 200, Height = 10 };
        if (choice.Type == LayerType.Calibration) layer = layer with { X = 0, Y = 0, Width = 240, Height = 240 };
        else layer = renderer.PlaceNewLayer(SelectedPage, layer, DateTime.Now);
        var background = (Color)ColorConverter.ConvertFromString(SelectedPage.Background);
        if ((background.R * 299 + background.G * 587 + background.B * 114) / 1000 > 160) layer = layer with { Color = "#17243F" };
        history.BreakGroup(); if (!ApplyPage(SelectedPage with { Layers = [.. SelectedPage.Layers, layer] })) return;
        selectedLayerId = layer.Id; RefreshLayersList(); FillLayerFields(); LayersList.ScrollIntoView(LayersList.SelectedItem);
        InspectorTabs.SelectedIndex = 1;
        EditorStatus.Text = "Элемент добавлен. Измените его свойства справа или перетащите на холсте.";
        if (layer.Type == LayerType.Text) Dispatcher.BeginInvoke(new Action(() => { LayerTextInput.Focus(); LayerTextInput.SelectAll(); }));
    }
    private void DeleteLayer(object sender, RoutedEventArgs e)
    {
        if (!CommitDraft() || selectedLayerId is null) return;
        var layers = SelectedPage.Layers.Where(layer => layer.Id != selectedLayerId).ToArray();
        history.BreakGroup(); if (!ApplyPage(SelectedPage with { Layers = layers })) return;
        selectedLayerId = null; RefreshLayersList(); FillLayerFields(); InspectorTabs.SelectedIndex = 0; EditorStatus.Text = "Элемент удалён · Ctrl+Z — вернуть";
    }
    private void MoveLayer(int delta)
    {
        if (!CommitDraft()) return;
        var layers = SelectedPage.Layers.ToList(); int from = layers.FindIndex(layer => layer.Id == selectedLayerId), to = from + delta;
        if (from < 0 || to < 0 || to >= layers.Count) return;
        history.BreakGroup(); (layers[from], layers[to]) = (layers[to], layers[from]); ApplyPage(SelectedPage with { Layers = layers.ToArray() });
    }
    private void MoveLayerUp(object sender, RoutedEventArgs e) => MoveLayer(-1);
    private void MoveLayerDown(object sender, RoutedEventArgs e) => MoveLayer(1);
    private void UndoEdit(object sender, RoutedEventArgs e) => RestoreHistory(false);
    private void RedoEdit(object sender, RoutedEventArgs e) => RestoreHistory(true);
    private void RestoreHistory(bool redo)
    {
        dragOrigin = null; EditorPreview.ReleaseMouseCapture();
        project = redo ? history.Redo() : history.Undo();
        liveAnimationId = editorAnimationId = Guid.Empty;
        renderer.Assets = project.Assets; sourceHub.SetProject(project); FillSourceFields();
        if (!project.Pages.Any(page => page.Id == selectedPageId)) selectedPageId = project.Pages[0].Id;
        if (!SelectedPage.Layers.Any(layer => layer.Id == selectedLayerId)) selectedLayerId = null;
        Synchronize(() => RotateCheck.IsChecked = project.AutoRotate);
        RefreshPagesList(); FillPageFields(); ValidationStatus.Text = ""; UpdateDocumentState();
        InspectorTabs.SelectedIndex = WorkspaceTabs.SelectedIndex == 1 && SelectedLayer is not null ? 1 : 0;
        EditorStatus.Text = redo ? "Действие повторено" : "Действие отменено";
    }
    private Point CanvasPoint(MouseEventArgs e)
    {
        var point = e.GetPosition(EditorPreview);
        return new Point(point.X * 240 / EditorPreview.ActualWidth, point.Y * 240 / EditorPreview.ActualHeight);
    }
    private void CanvasDown(object sender, MouseButtonEventArgs e)
    {
        if (!CommitDraft()) return;
        var point = CanvasPoint(e);
        resizeHandle = SelectedLayer is { Visible: true } chosen ? LayerResize.HitTest(chosen,point.X,point.Y,CanvasHandleRadius()) : ResizeHandle.None;
        if (resizeHandle != ResizeHandle.None)
        {
            history.BreakGroup(); dragOrigin = point; canvasDragStart = SelectedLayer; EditorPreview.Focus(); EditorPreview.CaptureMouse(); e.Handled = true; return;
        }
        var hit = SelectedPage.Layers.Reverse().FirstOrDefault(layer => layer.Visible && renderer.Bounds(layer, DateTime.Now).Contains((float)point.X, (float)point.Y));
        selectedLayerId = hit?.Id; RefreshLayersList(); FillLayerFields(); EditorPreview.Focus();
        InspectorTabs.SelectedIndex = hit is null ? 0 : 1;
        if (hit is null) return;
        history.BreakGroup(); dragOrigin = point; canvasDragStart = hit; EditorPreview.CaptureMouse(); e.Handled = true;
    }
    private void CanvasMove(object sender, MouseEventArgs e)
    {
        var point = CanvasPoint(e);
        if (dragOrigin is not Point origin || e.LeftButton != MouseButtonState.Pressed || SelectedLayer is not { } layer || canvasDragStart is not { } initial)
        {
            var handle = SelectedLayer is { Visible: true } selected ? LayerResize.HitTest(selected,point.X,point.Y,CanvasHandleRadius()) : ResizeHandle.None;
            EditorPreview.Cursor = handle switch { ResizeHandle.TopLeft or ResizeHandle.BottomRight => Cursors.SizeNWSE, ResizeHandle.TopRight or ResizeHandle.BottomLeft => Cursors.SizeNESW, ResizeHandle.Left or ResizeHandle.Right => Cursors.SizeWE, ResizeHandle.Top or ResizeHandle.Bottom => Cursors.SizeNS, _ => Cursors.Arrow }; return;
        }
        var bounds = renderer.Bounds(layer, DateTime.Now);
        var updated = resizeHandle != ResizeHandle.None
            ? LayerResize.Apply(initial,resizeHandle,point.X-origin.X,point.Y-origin.Y,(Keyboard.Modifiers & ModifierKeys.Shift)==0)
            : layer with { X = Math.Clamp(initial.X + (int)Math.Round(point.X-origin.X),0,Math.Max(0,240-(int)Math.Ceiling(bounds.Width))), Y = Math.Clamp(initial.Y + (int)Math.Round(point.Y-origin.Y),0,Math.Max(0,240-(int)Math.Ceiling(bounds.Height))) };
        if (updated == layer) return;
        editGroup = (resizeHandle == ResizeHandle.None ? "drag:" : "resize:") + layer.Id;
        ApplyPage(SelectedPage with { Layers = SelectedPage.Layers.Select(item => item.Id == layer.Id ? updated : item).ToArray() }); editGroup = null;
        Synchronize(() => { XInput.Text = updated.X.ToString(); YInput.Text = updated.Y.ToString(); WidthInput.Text = updated.Width.ToString(); HeightInput.Text = updated.Height.ToString(); });
    }
    private double CanvasHandleRadius() => 7*240/Math.Max(1,EditorViewbox.ActualWidth);
    private void CanvasUp(object sender, MouseButtonEventArgs e) { dragOrigin = null; canvasDragStart = null; resizeHandle = ResizeHandle.None; EditorPreview.ReleaseMouseCapture(); history.BreakGroup(); RefreshPagesList(); }
    private void CanvasLostCapture(object sender, MouseEventArgs e) { dragOrigin = null; canvasDragStart = null; resizeHandle = ResizeHandle.None; if (ready) history.BreakGroup(); }
    private void WorkspaceChanged(object sender, SelectionChangedEventArgs e) { if (ready && ReferenceEquals(e.Source, WorkspaceTabs)) { UpdateWorkspaceNavigation(); Render(); } }
}
