using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniDisplay.Core;

public enum LayerType { Text, Clock, Seconds, Weekday, Date, SecondsBar, Calibration, Image, Gif, DataText, Bar, Ring, Graph, Artwork, WeatherIcon }
public enum ImageFit { Contain, Cover, Stretch }

public sealed record PageLayer
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Текст";
    public LayerType Type { get; init; } = LayerType.Text;
    public string Text { get; init; } = "Новый текст";
    public int X { get; init; } = 18;
    public int Y { get; init; } = 20;
    public int Width { get; init; } = 204;
    public int Height { get; init; } = 4;
    public int FontSize { get; init; } = 18;
    public string FontFamily { get; init; } = "Segoe UI";
    public string Color { get; init; } = "#8EEDB5";
    public bool Bold { get; init; }
    public bool Visible { get; init; } = true;
    public string AssetId { get; init; } = "";
    public ImageFit Fit { get; init; } = ImageFit.Contain;
    public int CropLeft { get; init; }
    public int CropTop { get; init; }
    public int CropRight { get; init; }
    public int CropBottom { get; init; }
    public int AnimationFps { get; init; } = 20;
    public int RepeatCount { get; init; }
    public DataField DataField { get; init; } = DataField.CpuLoad;
    public double RangeMin { get; init; }
    public double RangeMax { get; init; } = 100;

    public void Validate()
    {
        if (Id == Guid.Empty || Name is null || Name.Length > 60 || Text is null || Text.Length > 300)
            throw new InvalidDataException("Некорректное имя или текст элемента.");
        if (!Enum.IsDefined(Type)) throw new InvalidDataException("Неизвестный тип элемента.");
        if (string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 100) throw new InvalidDataException("Некорректное название шрифта.");
        if (X is < 0 or > 239 || Y is < 0 or > 239) throw new InvalidDataException("Координаты: от 0 до 239.");
        if (Width is < 1 or > 240 || Height is < 1 or > 240 || FontSize is < 6 or > 96)
            throw new InvalidDataException("Размер: 1–240; размер шрифта: 6–96.");
        if (!DisplayProject.IsColor(Color)) throw new InvalidDataException("Некорректный цвет элемента.");
        if (!Enum.IsDefined(Fit) || !Enum.IsDefined(DataField) || AssetId is null) throw new InvalidDataException("Некорректный источник элемента.");
        if (Type is LayerType.Bar or LayerType.Ring or LayerType.Graph && !DataFields.IsNumeric(DataField) || Type == LayerType.Graph && DataField > DataField.RamTotal) throw new InvalidDataException("Для индикатора нужен числовой источник; графики используют показатели компьютера.");
        if (CropLeft is < 0 or > 95 || CropRight is < 0 or > 95 || CropTop is < 0 or > 95 || CropBottom is < 0 or > 95 || CropLeft + CropRight > 95 || CropTop + CropBottom > 95)
            throw new InvalidDataException("Обрезка должна оставлять не менее 5% изображения.");
        if (AnimationFps is < 1 or > 20 || RepeatCount is < 0 or > 1000) throw new InvalidDataException("Некорректные параметры анимации.");
        if (!double.IsFinite(RangeMin) || !double.IsFinite(RangeMax) || RangeMax <= RangeMin) throw new InvalidDataException("Максимум шкалы должен быть больше минимума.");
    }

    public PageLayer Duplicate() => this with { Id = Guid.NewGuid() };
}

public sealed record DisplayPage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Новая страница";
    public int DurationSeconds { get; init; } = 15;
    public bool Enabled { get; init; } = true;
    public string Background { get; init; } = "#101C22";
    public PageLayer[] Layers { get; init; } = [];

    [JsonIgnore] public string Summary => $"{DurationSeconds} с" + (Enabled ? "" : " · выключена");

    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 60)
            throw new InvalidDataException("Название страницы: от 1 до 60 символов.");
        if (DurationSeconds is < 1 or > 3600) throw new InvalidDataException("Время показа: от 1 до 3600 секунд.");
        if (!DisplayProject.IsColor(Background)) throw new InvalidDataException("Некорректный цвет фона.");
        if (Layers is null || Layers.Length > 64 || Layers.Any(layer => layer is null))
            throw new InvalidDataException("На странице допускается до 64 элементов.");
        if (Layers.Select(layer => layer.Id).Distinct().Count() != Layers.Length)
            throw new InvalidDataException("Идентификаторы элементов должны различаться.");
        foreach (var layer in Layers) layer.Validate();
    }

    public DisplayPage Duplicate() => this with
    {
        Id = Guid.NewGuid(), Name = (Name.Length > 52 ? Name[..52] : Name) + " — копия",
        Layers = Layers.Select(layer => layer.Duplicate()).ToArray()
    };
}

public static class PageTemplates
{
    public static DisplayPage Clock(string title = "МОЙ ДИСПЛЕЙ", string accent = "#8EEDB5", string background = "#101C22") => new()
    {
        Name = "Часы", Background = background,
        Layers =
        [
            new() { Name = "Подпись", Text = title, X = 18, Y = 20, FontSize = 11, Bold = true, Color = accent },
            new() { Name = "Время", Type = LayerType.Clock, X = 15, Y = 60, FontSize = 57, Bold = true, Color = "#FFFFFF" },
            new() { Name = "Секунды", Type = LayerType.Seconds, X = 20, Y = 125, FontSize = 19, Color = accent },
            new() { Name = "День недели", Type = LayerType.Weekday, X = 18, Y = 165, FontSize = 17, Color = "#FFFFFF" },
            new() { Name = "Дата", Type = LayerType.Date, X = 18, Y = 193, FontSize = 13, Color = "#A4B9C3" },
            new() { Name = "Шкала секунд", Type = LayerType.SecondsBar, X = 19, Y = 222, Width = 202, Height = 3, Color = accent }
        ]
    };

    public static DisplayPage Text() => new()
    {
        Name = "Текст", Layers = [new() { Text = "Привет!", X = 25, Y = 90, FontSize = 32, Bold = true }]
    };

    public static DisplayPage Calibration(string accent = "#8EEDB5", string background = "#101C22") => new()
    {
        Name = "Проверка цветов", Background = background,
        Layers = [new() { Name = "Тест цветов", Type = LayerType.Calibration, X = 0, Y = 0, Width = 240, Height = 240, Color = accent }]
    };
}

public sealed record DisplayProject
{
    public int Version { get; init; } = 3;
    public int FramesPerSecond { get; init; } = 20;
    public bool AutoRotate { get; init; } = true;
    public DisplayPage[] Pages { get; init; } = [PageTemplates.Clock(), PageTemplates.Calibration()];
    public ProjectAsset[] Assets { get; init; } = [];
    public SourceSettings Sources { get; init; } = new();

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };

    public void Validate()
    {
        if (Version != 3) throw new InvalidDataException("Неизвестная версия проекта.");
        if (FramesPerSecond is < 1 or > 20) throw new InvalidDataException("Частота: от 1 до 20 кадров/с.");
        if (Pages is null || Pages.Length is < 1 or > 64 || Pages.Any(page => page is null))
            throw new InvalidDataException("В проекте должно быть от 1 до 64 страниц.");
        if (!Pages.Any(page => page.Enabled)) throw new InvalidDataException("Оставьте хотя бы одну страницу включённой.");
        if (Pages.Select(page => page.Id).Distinct().Count() != Pages.Length)
            throw new InvalidDataException("Идентификаторы страниц должны различаться.");
        foreach (var page in Pages) page.Validate();
        if (Assets is null || Assets.Length > 128 || Assets.Any(asset => asset is null) || Assets.Select(asset => asset.Id).Distinct().Count() != Assets.Length)
            throw new InvalidDataException("Некорректный список ресурсов.");
        if (Assets.Sum(asset => (long)(asset.Data?.Length ?? 0)) > ProjectAsset.MaxTotalBytes) throw new InvalidDataException("Ресурсы проекта превышают 32 МиБ.");
        foreach (var asset in Assets) asset.Validate();
        foreach (var layer in Pages.SelectMany(page => page.Layers).Where(layer => layer.Type is LayerType.Image or LayerType.Gif))
            if (!Assets.Any(asset => asset.Id == layer.AssetId)) throw new InvalidDataException("Не найден ресурс изображения: " + layer.Name);
        if (Sources is null) throw new InvalidDataException("Не заданы настройки источников.");
        Sources.Validate();
    }

    public static bool IsColor(string? color) => color is { Length: 7 } && color[0] == '#' &&
        color.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    public static DisplayProject Load(string path)
    {
        bool packaged;
        using (var signature = File.OpenRead(path))
            packaged = signature.ReadByte() == 'P' && signature.ReadByte() == 'K';
        if (!packaged && new FileInfo(path).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("Файл проекта слишком большой: допускается до 8 МиБ.");
        var project = packaged ? ProjectPackage.Load(path) : ParseJson(File.ReadAllText(path));
        // Retire the built-in provider footer from previous weather templates.
        return project with { Pages = project.Pages.Select(page => page with { Layers = page.Layers.Where(layer =>
            !(layer.X == 16 && layer.Y == 228 && layer.FontSize == 9 &&
              (layer.Type == LayerType.Text && layer.Name == "Open-Meteo" && layer.Text == "Open-Meteo" ||
               layer.Type == LayerType.DataText && layer.Name == "Источник погоды" && layer.DataField == DataField.WeatherSource))).ToArray() }).ToArray() };
    }

    internal static DisplayProject ParseJson(string json, bool validate = true)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        int version = 1;
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Проект должен быть JSON-объектом.");
        if (document.RootElement.TryGetProperty("Version", out var field) &&
            (field.ValueKind != JsonValueKind.Number || !field.TryGetInt32(out version)))
            throw new InvalidDataException("Некорректная версия проекта.");
        DisplayProject project;
        if (version == 1)
        {
            var legacy = document.RootElement.Deserialize<LegacyProject>() ?? throw new InvalidDataException("Пустой проект.");
            if (legacy.Title is null || legacy.Title.Length > 40 || !IsColor(legacy.Accent) || !IsColor(legacy.Background))
                throw new InvalidDataException("Некорректное оформление старого проекта.");
            project = new DisplayProject
            {
                FramesPerSecond = legacy.FramesPerSecond, AutoRotate = legacy.AutoRotate,
                Pages = [PageTemplates.Clock(legacy.Title, legacy.Accent, legacy.Background), PageTemplates.Calibration(legacy.Accent, legacy.Background)]
            };
        }
        else if (version is 2 or 3)
            project = (document.RootElement.Deserialize<DisplayProject>(JsonOptions) ?? throw new InvalidDataException("Пустой проект.")) with { Version = 3 };
        else throw new InvalidDataException("Неизвестная версия проекта.");
        if (validate) project.Validate();
        return project;
    }

    public void Save(string path)
    {
        Validate();
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) { ProjectPackage.Save(this, path); return; }
        string json = JsonSerializer.Serialize(this, JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 8 * 1024 * 1024) throw new InvalidDataException("Для ресурсов такого размера сохраните проект пакетом .mdisplay.");
        string full = Path.GetFullPath(path);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record LegacyProject
    {
        public string Title { get; init; } = "МОЙ ДИСПЛЕЙ";
        public string Accent { get; init; } = "#8EEDB5";
        public string Background { get; init; } = "#101C22";
        public int FramesPerSecond { get; init; } = 20;
        public bool AutoRotate { get; init; } = true;
    }
}
