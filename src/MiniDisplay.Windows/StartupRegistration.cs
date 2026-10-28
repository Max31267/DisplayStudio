using Microsoft.Win32;

namespace MiniDisplay.Windows;

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DisplayStudio";
    public static string Command(string executable)
    {
        string path = Path.GetFullPath(executable);
        if (!path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase) || path.IndexOfAny(['"','\r','\n']) >= 0)
            throw new ArgumentException("Некорректный путь приложения.");
        string command = "\"" + path + "\" --autostart";
        if (command.Length > 260) throw new IOException("Путь слишком длинный для автозагрузки. Переместите приложение ближе к корню диска.");
        return command;
    }
    public static string Read()
    { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(ValueName) as string ?? ""; }
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey,true);
        if (enabled) key.SetValue(ValueName,Command(Environment.ProcessPath ?? throw new IOException("Не найден путь приложения.")),RegistryValueKind.String);
        else key.DeleteValue(ValueName,false);
    }
}
