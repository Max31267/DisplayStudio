using System.Text.Json;

namespace MiniDisplay.Windows;

// Machine-specific executable paths never come from imported projects.
public sealed record LocalPreferences
{
    public string CodexExecutable { get; init; } = "";
    public bool RestoreProject { get; init; } = true;
    public bool AutoConnect { get; init; } = true;
    public string LastProjectPath { get; init; } = "";
    public string LastPort { get; init; } = "";
    public Guid LastPageId { get; init; }
    public int Brightness { get; init; } = 80;
    private static readonly object Gate = new();
    public static void Update(Func<LocalPreferences, LocalPreferences> change)
    { lock (Gate) { var current = Load(); var updated = change(current); if (updated != current) updated.Save(); } }
    private static string FilePath => Path.Combine(Environment.GetEnvironmentVariable("DISPLAYSTUDIO_PROFILE_DIR") is {Length:>0} custom ? custom : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MiniDisplay"),"settings.json");
    public static LocalPreferences Load()
    {
        try { return File.Exists(FilePath) && new FileInfo(FilePath).Length <= 16 * 1024 ? JsonSerializer.Deserialize<LocalPreferences>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save()
    {
        string path = FilePath, temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { File.WriteAllText(temporary,JsonSerializer.Serialize(this)); File.Move(temporary,path,true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
