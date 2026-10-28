using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace MiniDisplay.Core;

public sealed record ProjectAsset
{
    public const int MaxBytes = 8 * 1024 * 1024;
    public const int MaxTotalBytes = 32 * 1024 * 1024;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public byte[] Data { get; init; } = [];
    public static ProjectAsset Create(string name, byte[] data) => new() { Id = Convert.ToHexStringLower(SHA256.HashData(data)), Name = Path.GetFileName(name), Data = data };
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 260 || Data is null || Data.Length is < 1 or > MaxBytes ||
            Id != Convert.ToHexStringLower(SHA256.HashData(Data))) throw new InvalidDataException("Повреждён или слишком велик ресурс проекта.");
    }
}

public static class ProjectPackage
{
    public static void Save(DisplayProject project, string path)
    {
        project.Validate();
        string target = Path.GetFullPath(path), temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = File.Create(temporary))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var manifest = project with { Assets = project.Assets.Select(asset => asset with { Data = [] }).ToArray() };
                string json = JsonSerializer.Serialize(manifest, DisplayProject.JsonOptions);
                if (System.Text.Encoding.UTF8.GetByteCount(json) > 8 * 1024 * 1024) throw new InvalidDataException("Описание проекта превышает 8 МиБ.");
                using (var writer = new StreamWriter(archive.CreateEntry("project.json").Open())) writer.Write(json);
                foreach (var asset in project.Assets)
                {
                    using var output = archive.CreateEntry("assets/" + asset.Id, CompressionLevel.Optimal).Open(); output.Write(asset.Data);
                }
            }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static DisplayProject Load(string path)
    {
        if (new FileInfo(path).Length > ProjectAsset.MaxTotalBytes + 8 * 1024 * 1024) throw new InvalidDataException("Пакет проекта слишком велик.");
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count is < 1 or > 129 || archive.Entries.Select(entry => entry.FullName).Distinct().Count() != archive.Entries.Count)
            throw new InvalidDataException("Некорректный пакет проекта.");
        var manifest = archive.GetEntry("project.json") ?? throw new InvalidDataException("В пакете нет project.json.");
        var project = DisplayProject.ParseJson(System.Text.Encoding.UTF8.GetString(Read(manifest, 8 * 1024 * 1024)), false);
        if (project.Assets is null || project.Assets.Length > 128 || project.Assets.Any(asset => asset is null)) throw new InvalidDataException("Некорректные ресурсы пакета.");
        var allowed = project.Assets.Select(asset => "assets/" + asset.Id).Append("project.json").ToHashSet(StringComparer.Ordinal);
        if (archive.Entries.Any(entry => !allowed.Contains(entry.FullName))) throw new InvalidDataException("В пакете есть неизвестные файлы.");
        long total = 0;
        var assets = project.Assets.Select(asset =>
        {
            if (asset.Id is null || asset.Id.Length != 64 || asset.Id.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("Некорректный идентификатор ресурса.");
            var entry = archive.GetEntry("assets/" + asset.Id) ?? throw new InvalidDataException("В пакете отсутствует ресурс.");
            total += entry.Length;
            if (total > ProjectAsset.MaxTotalBytes) throw new InvalidDataException("Ресурсы превышают 32 МиБ.");
            return asset with { Data = Read(entry, ProjectAsset.MaxBytes) };
        }).ToArray();
        project = project with { Assets = assets }; project.Validate(); return project;
    }
    private static byte[] Read(ZipArchiveEntry entry, int limit)
    {
        if (entry.Length > limit) throw new InvalidDataException("Файл внутри пакета слишком велик.");
        using var input = entry.Open(); using var output = new MemoryStream(); byte[] buffer = new byte[8192]; int read;
        while ((read = input.Read(buffer)) > 0) { if (output.Length + read > limit) throw new InvalidDataException("Распакованный файл слишком велик."); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
}
