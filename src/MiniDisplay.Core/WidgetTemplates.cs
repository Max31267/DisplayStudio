namespace MiniDisplay.Core;

public static class WidgetTemplates
{
    private static PageLayer Label(string text, int x, int y, int size = 12) => new() { Name = text, Text = text, X = x, Y = y, FontSize = size, Color = "#A4B9C3" };
    private static PageLayer Value(string name, DataField field, int x, int y, int size = 20, string text = "{value}") => new() { Name = name, Type = LayerType.DataText, DataField = field, Text = text, X = x, Y = y, Width = 224 - x, Height = Math.Min(240 - y, size + 10), FontSize = size, Color = "#FFFFFF" };
    public static DisplayPage Weather() => new()
    {
        Name = "Часы и погода", Layers = [new() { Name = "Часы", Type = LayerType.Clock, X = 15, Y = 8, FontSize = 36, Bold = true, Color = "#FFFFFF" },
            new() { Name = "Дата", Type = LayerType.Date, X = 16, Y = 56, FontSize = 9, Color = "#A4B9C3" },
            Value("Город", DataField.WeatherCity, 16, 76, 14), Value("Температура", DataField.WeatherTemperature, 16, 102, 30),
            new() { Name = "Погодная иконка", Type = LayerType.WeatherIcon, X = 166, Y = 102, Width = 54, Height = 54 },
            Value("Условия", DataField.WeatherSummary, 16, 147, 13), Value("Ощущается", DataField.WeatherFeelsLike, 16, 174, 13, "Ощущается {value}"),
            Value("Влажность", DataField.WeatherHumidity, 16, 200, 13, "Влажность {value}")]
    };
    public static DisplayPage Computer() => new()
    {
        Name = "Компьютер", Layers = [Label("КОМПЬЮТЕР", 15, 12), Label("CPU", 15, 39), Value("Загрузка CPU", DataField.CpuLoad, 65, 34, 20),
            new() { Name = "График CPU", Type = LayerType.Graph, DataField = DataField.CpuLoad, X = 15, Y = 69, Width = 210, Height = 48 },
            Label("GPU", 15, 129), Value("Загрузка GPU", DataField.GpuLoad, 65, 124, 20), Label("RAM", 15, 166), Value("Память", DataField.RamLoad, 65, 161, 20),
            new() { Name = "Шкала памяти", Type = LayerType.Bar, DataField = DataField.RamLoad, X = 15, Y = 196, Width = 210, Height = 7 },
            Value("Температура CPU", DataField.CpuTemperature, 15, 215, 11, "CPU {value}"), Value("Температура GPU", DataField.GpuTemperature, 125, 215, 11, "GPU {value}")]
    };
    public static DisplayPage Music() => new()
    {
        Name = "Музыка", Layers = [new() { Name = "Обложка", Type = LayerType.Artwork, X = 64, Y = 12, Width = 112, Height = 112 },
            Value("Трек", DataField.MusicTitle, 14, 135, 17), Value("Исполнитель", DataField.MusicArtist, 14, 162, 13), Value("Состояние", DataField.MusicState, 14, 186, 11),
            new() { Name = "Прогресс", Type = LayerType.Bar, DataField = DataField.MusicProgress, X = 14, Y = 212, Width = 212, Height = 5 },
            Value("Позиция", DataField.MusicPosition, 14, 224, 9), Value("Длительность", DataField.MusicDuration, 177, 224, 9)]
    };
    public static DisplayPage Codex() => new()
    {
        Name = "Лимиты Codex", Layers = [Label("CODEX · ОСТАЛОСЬ", 15, 12),
            new() { Name = "Основной лимит", Type = LayerType.Ring, DataField = DataField.CodexPrimaryRemaining, X = 18, Y = 45, Width = 88, Height = 88 },
            new() { Name = "Второй лимит", Type = LayerType.Ring, DataField = DataField.CodexSecondaryRemaining, X = 133, Y = 45, Width = 88, Height = 88 },
            Value("Основное окно", DataField.CodexPrimaryWindow, 15, 148, 12), Value("Сброс основного", DataField.CodexPrimaryReset, 15, 172, 11),
            Value("Второе окно", DataField.CodexSecondaryWindow, 15, 199, 12), Value("Сброс второго", DataField.CodexSecondaryReset, 15, 221, 10)]
    };
}
