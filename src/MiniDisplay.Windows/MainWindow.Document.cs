using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;

namespace MiniDisplay.Windows;

public partial class MainWindow
{
    private string? currentProjectPath;
    private bool DocumentDirty => !MiniDisplay.Core.ProjectHistory.Equivalent(project, savedSnapshot) || HasInvalidInputs();

    private bool SaveDocument(bool saveAs)
    {
        if (!CommitDraft()) return false;
        string? path = currentProjectPath;
        if (saveAs || path is null)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Переносимый проект с ресурсами (*.mdisplay)|*.mdisplay|JSON-проект (*.mdisplay.json)|*.mdisplay.json", DefaultExt = ".mdisplay",
                FileName = path is null ? "Мой дисплей.mdisplay" : Path.GetFileName(path)
            };
            if (path is not null) dialog.InitialDirectory = Path.GetDirectoryName(path);
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }
        try
        {
            project.Save(path); currentProjectPath = path; savedSnapshot = project;
            RememberProject(Path.GetFullPath(path));
            ProjectNameLabel.Text = ProjectDisplayName(path);
            history.BreakGroup(); UpdateDocumentState(); EditorStatus.Text = "Проект сохранён";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { ShowError("Не удалось сохранить проект. " + ex.Message); return false; }
    }
    private static string ProjectDisplayName(string path) => path.EndsWith(".mdisplay.json", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(path)[..^14] : Path.GetFileNameWithoutExtension(path);
    private bool ConfirmSaveChanges()
    {
        if (!DocumentDirty) return true;
        var result = System.Windows.MessageBox.Show(this, "Сохранить изменения в текущем проекте?", "Есть несохранённые изменения", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.No || result == MessageBoxResult.Yes && SaveDocument(false);
    }
    private void RequestExit()
    {
        if(firmwareBusy) { ShowError("Дождитесь завершения прошивки или прервите её в настройках."); return; }
        if (!ConfirmSaveChanges()) return;
        if(currentProjectPath is not null) RememberProject(currentProjectPath);
        exiting = true; Close();
    }
    private void SaveProjectAs(object sender, RoutedEventArgs e) => SaveDocument(true);
    private void OpenProjectMenu(object sender, RoutedEventArgs e)
    {
        ProjectMenu.PlacementTarget = (Button)sender; ProjectMenu.Placement = PlacementMode.Bottom; ProjectMenu.IsOpen = true;
    }
    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void HideWindow(object sender, RoutedEventArgs e) => Close();
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        bool input = Keyboard.FocusedElement is TextBoxBase;
        if (e.Key == Key.S) { SaveDocument((Keyboard.Modifiers & ModifierKeys.Shift) != 0); e.Handled = true; }
        else if (e.Key == Key.O) { OpenProject(this, new RoutedEventArgs()); e.Handled = true; }
        else if (!input && e.Key == Key.Z) { RestoreHistory(false); e.Handled = true; }
        else if (!input && e.Key == Key.Y) { RestoreHistory(true); e.Handled = true; }
    }
}
