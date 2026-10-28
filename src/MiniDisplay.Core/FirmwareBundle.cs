using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MiniDisplay.Core;

public sealed record FirmwareProfile(string Id, string Name, string File, string Sha256)
{ public override string ToString() => Name; }
public sealed record FlashCommand(string Executable, string[] Arguments);
public sealed record FirmwareBundle
{
    public string Version { get; init; } = "";
    public string Chip { get; init; } = "esp32c3";
    public string Tool { get; init; } = "Tools/esptool/esptool.exe";
    public string ToolSha256 { get; init; } = "";
    public FirmwareProfile[] Profiles { get; init; } = [];
    public static FirmwareBundle Load(string root)
    {
        string path = Path.Combine(root,"Firmware","manifest.json");
        if (!File.Exists(path)) throw new FileNotFoundException("Файлы прошивки не найдены. Распакуйте полный комплект программы.");
        if (new FileInfo(path).Length > 64*1024) throw new InvalidDataException("Описание прошивки повреждено.");
        var bundle = JsonSerializer.Deserialize<FirmwareBundle>(File.ReadAllText(path)) ?? throw new InvalidDataException("Пустое описание прошивки.");
        if (bundle.Chip!="esp32c3" || bundle.Version is null || bundle.Version.Length>60 || bundle.Profiles is null || bundle.Profiles.Length is <1 or >8 ||
            bundle.Profiles.Any(item=>item is null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length>40 || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length>140 || !Hash(item.Sha256)) ||
            bundle.Profiles.Select(item=>item.Id).Distinct().Count()!=bundle.Profiles.Length || !Hash(bundle.ToolSha256)) throw new InvalidDataException("Некорректный комплект прошивки.");
        Resolve(root,bundle.Tool); foreach(var profile in bundle.Profiles) Resolve(root,profile.File);
        return bundle;
    }
    public FlashCommand Prepare(string root, string profileId, string port)
    {
        if (!ValidPort(port)) throw new ArgumentException("Выберите COM-порт ESP32-C3.");
        var profile = Profiles.FirstOrDefault(item=>item.Id==profileId) ?? throw new InvalidDataException("Неизвестный вариант дисплея.");
        string tool = Resolve(root,Tool), image = Resolve(root,profile.File);
        Verify(tool,ToolSha256,128*1024*1024); Verify(image,profile.Sha256,4*1024*1024);
        using(var input=File.OpenRead(image)) if(input.Length<24 || input.ReadByte()!=0xe9) throw new InvalidDataException("Файл не является загрузочной прошивкой ESP32-C3.");
        return new(tool,["--chip","esp32c3","--port",port,"--baud","460800","write-flash","0x0",image]);
    }
    public static bool ValidPort(string? port) => port is not null && Regex.IsMatch(port,@"\ACOM[1-9][0-9]{0,3}\z",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    private static bool Hash(string? value) => value is {Length:64} && value.All(char.IsAsciiHexDigit);
    private static string Resolve(string root,string relative)
    {
        if(string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("Некорректный путь в комплекте.");
        string folder=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar, path=Path.GetFullPath(Path.Combine(root,relative));
        if(!path.StartsWith(folder,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Файл выходит за пределы комплекта.");
        return path;
    }
    private static void Verify(string path,string hash,long limit)
    {
        var info=new FileInfo(path);
        if(!info.Exists || info.Length<1 || info.Length>limit) throw new InvalidDataException("Отсутствует или повреждён файл: "+info.Name);
        using var stream=File.OpenRead(path);
        if(!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Проверка целостности не пройдена: "+info.Name+". Распакуйте комплект заново.");
    }
}
