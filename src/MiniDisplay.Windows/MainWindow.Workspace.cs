using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MiniDisplay.Core;

namespace MiniDisplay.Windows;

public partial class MainWindow
{
    private double canvasZoom = 1;
    private Point? reorderOrigin;
    private Guid? reorderId;
    private ListBox? reorderList;

    private void NavigateWorkspace(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse((sender as FrameworkElement)?.Tag as string, out int index) || index is < 0 or > 4) return;
        if (!CommitDraft()) { UpdateWorkspaceNavigation(); return; }
        WorkspaceTabs.SelectedIndex = index;
    }
    private void UpdateWorkspaceNavigation()
    {
        int index = WorkspaceTabs.SelectedIndex;
        bool editor = index == 1, device = index >= 2;
        EditorNavigation.IsChecked = editor; PlaybackNavigation.IsChecked = index == 0;
        DeviceNavigation.IsChecked = index == 2; SourcesNavigation.IsChecked = index == 3; sourceHub.SetInspecting(index == 3);
        SettingsNavigation.IsChecked = index == 4;
        PageSidebar.Visibility = InspectorPanel.Visibility = device ? Visibility.Collapsed : Visibility.Visible;
        PageSidebarColumn.Width = new GridLength(device ? 0 : 260);
        InspectorColumn.Width = new GridLength(device ? 0 : 326);
        Grid.SetColumn(WorkspaceHost, device ? 0 : 1); Grid.SetColumnSpan(WorkspaceHost, device ? 3 : 1);
        ElementsPanel.Visibility = editor ? Visibility.Visible : Visibility.Collapsed;
        ElementsRow.Height = editor ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PagesList.MaxHeight = editor ? 260 : double.PositiveInfinity;
        PagesRow.Height = editor ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        PagesHeading.Text = editor ? "Страницы" : "Порядок показа";
        WorkspaceHint.Text = index switch { 0 => "Порядок, длительность и смена страниц", 1 => "Соберите содержимое каждой страницы", 3 => "Погода, компьютер, музыка и Codex", 4 => "Запуск программы и прошивка ESP32-C3", _ => "Подключение, эмулятор и диагностика" };
        EditorStatus.Text = index switch { 0 => "Нажмите страницу слева для показа или включите автоматическую смену", 1 => "Выберите элемент на холсте или нажмите «Добавить элемент»", 3 => "Настройте источники и добавьте готовые страницы", 4 => "Настройки сохраняются на этом компьютере", _ => "Выберите эмулятор или COM-порт и нажмите «Подключить»" };
        InspectorTabs.SelectedIndex = editor && SelectedLayer is not null ? 1 : 0;
    }
    private void ShowPageSettings(object sender, RoutedEventArgs e)
    {
        if (!CommitDraft()) return;
        selectedLayerId = null; RefreshLayersList(); FillLayerFields(); InspectorTabs.SelectedIndex = 0;
    }
    private void DuplicateLayer(object sender, RoutedEventArgs e)
    {
        if (!CommitDraft() || SelectedLayer is not { } original) return;
        if (SelectedPage.Layers.Length >= 64) { ValidationStatus.Text = "На странице уже 64 элемента."; return; }
        var copy = original.Duplicate() with { Name = UniqueLayerName(original.Name + " — копия"), X = Math.Min(239, original.X + 8), Y = Math.Min(239, original.Y + 8) };
        history.BreakGroup();
        if (!ApplyPage(SelectedPage with { Layers = [.. SelectedPage.Layers, copy] })) return;
        selectedLayerId = copy.Id; RefreshLayersList(); FillLayerFields(); InspectorTabs.SelectedIndex = 1;
        if (copy.Type == LayerType.Gif) { editorAnimationId = Guid.Empty; if (playback.CurrentPageId == selectedPageId) liveAnimationId = Guid.Empty; }
        LayersList.ScrollIntoView(LayersList.SelectedItem);
    }
    private string UniqueLayerName(string name)
    {
        name = name.Length > 52 ? name[..52] : name;
        if (!SelectedPage.Layers.Any(layer => layer.Name == name)) return name;
        int suffix = 2; while (SelectedPage.Layers.Any(layer => layer.Name == $"{name} {suffix}")) suffix++;
        return $"{name} {suffix}";
    }

    private void ShowEditorTool(object sender, RoutedEventArgs e) => WorkspaceTabs.SelectedIndex = 1;
    private void ShowPageColors(object sender, RoutedEventArgs e)
    {
        InspectorTabs.SelectedIndex = 0; BackgroundPicker.BringIntoView();
    }
    private void OpenLayerMenu(object sender, RoutedEventArgs e)
    {
        LayerMenu.PlacementTarget = (Button)sender; LayerMenu.IsOpen = true;
    }
    private void OpenPageMenu(object sender, RoutedEventArgs e)
    {
        PageMenu.PlacementTarget = (Button)sender; PageMenu.IsOpen = true;
    }
    private void QuickAddLayer(object sender, RoutedEventArgs e)
    {
        if (!Enum.TryParse<LayerType>((sender as FrameworkElement)?.Tag as string, out var type)) return;
        LayerTemplate.SelectedItem = LayerTemplate.Items.Cast<LayerChoice>().First(choice => choice.Type == type);
        AddLayer(sender, e); WorkspaceTabs.SelectedIndex = 1;
    }
    private void QuickAddPage(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse((sender as FrameworkElement)?.Tag as string, out int index) || index is < 0 or > 7) return;
        if (index >= 4) { if (CommitDraft()) InsertPage(index switch { 4 => WidgetTemplates.Weather(), 5 => WidgetTemplates.Computer(), 6 => WidgetTemplates.Music(), _ => WidgetTemplates.Codex() }); return; }
        PageTemplate.SelectedIndex = index; AddPage(sender, e);
    }
    private void ToggleLayerVisibility(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is not Guid id || !CommitDraft()) return;
        var layer = SelectedPage.Layers.FirstOrDefault(item => item.Id == id);
        if (layer is null) return;
        history.BreakGroup();
        var updated = layer with { Visible = !layer.Visible };
        if (!ApplyPage(SelectedPage with { Layers = SelectedPage.Layers.Select(item => item.Id == id ? updated : item).ToArray() })) return;
        if (selectedLayerId == id) Synchronize(() => LayerVisible.IsChecked = updated.Visible);
    }
    private void StepNumber(object sender, RoutedEventArgs e)
    {
        string[] parts = ((sender as FrameworkElement)?.Tag as string ?? "").Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int delta)) return;
        (TextBox? Input, int Min, int Max, int Current) field = parts[0] switch
        {
            "DurationInput" => (DurationInput, 1, 3600, SelectedPage.DurationSeconds),
            "XInput" => (XInput, 0, 239, SelectedLayer?.X ?? 0),
            "YInput" => (YInput, 0, 239, SelectedLayer?.Y ?? 0),
            "FontInput" => (FontInput, 6, 96, SelectedLayer?.FontSize ?? 18),
            _ => (null, 0, 0, 0)
        };
        if (field.Input is null) return;
        long value = long.TryParse(field.Input.Text, out var parsed) ? Math.Clamp(parsed, field.Min, field.Max) : field.Current;
        field.Input.Text = Math.Clamp(value + delta, field.Min, field.Max).ToString();
    }
    private void ZoomIn(object sender, RoutedEventArgs e) => SetCanvasZoom(canvasZoom + 0.25);
    private void ZoomOut(object sender, RoutedEventArgs e) => SetCanvasZoom(canvasZoom - 0.25);
    private void FitCanvas(object sender, RoutedEventArgs e)
    {
        SetCanvasZoom(1); EditorScroll.ScrollToHome();
    }
    private void SetCanvasZoom(double value)
    {
        canvasZoom = Math.Clamp(value, 0.5, 2);
        ZoomLabel.Text = $"{canvasZoom * 100:0}%"; UpdateCanvasViewport();
    }
    private void UpdateCanvasViewport()
    {
        if (WorkspaceTabs.SelectedIndex != 1 || !EditorScroll.IsLoaded || EditorScroll.ViewportWidth <= 0 || EditorScroll.ViewportHeight <= 0) return;
        double fitted = Math.Clamp(Math.Min(EditorScroll.ViewportWidth - 48, EditorScroll.ViewportHeight - 48), 120, 520);
        double size = fitted * canvasZoom;
        if (Math.Abs(EditorViewbox.Width - size) > 0.25) EditorViewbox.Width = EditorViewbox.Height = size;
    }
    private void SetConnectionBadge(string message, bool connected, bool recovering = false)
    {
        ConnectionStatus.Text = message;
        ConnectionSummary.Text = message;
        ConnectionIndicator.Fill = new SolidColorBrush(connected ? Color.FromRgb(40, 201, 134) : recovering ? Color.FromRgb(228, 161, 42) : Color.FromRgb(166, 178, 197));
    }
    private static T? FindParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T found) return found;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }
    private void ReorderDown(object sender, MouseButtonEventArgs e)
    {
        reorderOrigin = null; reorderId = null; reorderList = null;
        if (sender is not ListBox list || FindParent<Button>(e.OriginalSource as DependencyObject) is not null) return;
        if (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is not ListBoxItem { DataContext: EditorEntry entry }) return;
        reorderOrigin = e.GetPosition(list); reorderId = entry.Id; reorderList = list;
    }
    private void ReorderMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { reorderOrigin = null; return; }
        if (reorderOrigin is not Point origin || reorderId is not Guid id || reorderList is null) return;
        var point = e.GetPosition(reorderList);
        if (Math.Abs(point.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var list = reorderList; reorderOrigin = null;
        if (!CommitDraft()) return;
        string format = ReferenceEquals(list, PagesList) ? "DisplayStudio.Page" : "DisplayStudio.Layer";
        var result = DragDrop.DoDragDrop(list, new DataObject(format, id), DragDropEffects.Move);
        if (result != DragDropEffects.Move) return;
        if (ReferenceEquals(list, PagesList) && project.Pages.Any(page => page.Id == id))
        {
            selectedPageId = id; selectedLayerId = null;
            RefreshPagesList(); FillPageFields(); InspectorTabs.SelectedIndex = 0;
        }
        else if (ReferenceEquals(list, LayersList) && SelectedPage.Layers.Any(layer => layer.Id == id))
        {
            selectedLayerId = id; RefreshLayersList(); FillLayerFields(); InspectorTabs.SelectedIndex = 1;
        }
    }
    private void ReorderOver(object sender, DragEventArgs e)
    {
        string format = ReferenceEquals(sender, PagesList) ? "DisplayStudio.Page" : "DisplayStudio.Layer";
        e.Effects = e.Data.GetDataPresent(format) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true;
    }
    private void PagesDropped(object sender, DragEventArgs e) => DropReorder(PagesList, "DisplayStudio.Page", e);
    private void LayersDropped(object sender, DragEventArgs e) => DropReorder(LayersList, "DisplayStudio.Layer", e);
    private void DropReorder(ListBox list, string format, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(format) || e.Data.GetData(format) is not Guid id || !CommitDraft()) return;
        Guid? targetId = (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem)?.DataContext is EditorEntry entry ? entry.Id : null;
        history.BreakGroup();
        if (ReferenceEquals(list, PagesList))
        {
            var pages = project.Pages.ToList(); int from = pages.FindIndex(page => page.Id == id);
            int to = targetId is Guid target ? pages.FindIndex(page => page.Id == target) : pages.Count - 1;
            if (from < 0 || to < 0 || from == to) return;
            var moving = pages[from]; pages.RemoveAt(from); pages.Insert(to, moving);
            ApplyProject(project with { Pages = pages.ToArray() });
        }
        else
        {
            var layers = SelectedPage.Layers.ToList(); int from = layers.FindIndex(layer => layer.Id == id);
            int to = targetId is Guid target ? layers.FindIndex(layer => layer.Id == target) : layers.Count - 1;
            if (from < 0 || to < 0 || from == to) return;
            var moving = layers[from]; layers.RemoveAt(from); layers.Insert(to, moving);
            ApplyPage(SelectedPage with { Layers = layers.ToArray() });
        }
        EditorStatus.Text = "Порядок изменён · Ctrl+Z — отменить";
    }
}
