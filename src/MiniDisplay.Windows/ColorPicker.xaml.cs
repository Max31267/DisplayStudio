using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MiniDisplay.Core;
using Forms = System.Windows.Forms;

namespace MiniDisplay.Windows;

public partial class ColorPicker : UserControl
{
    public static readonly DependencyProperty ColorHexProperty = DependencyProperty.Register(
        nameof(ColorHex), typeof(string), typeof(ColorPicker),
        new FrameworkPropertyMetadata("#8EEDB5", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed),
        value => value is string color && DisplayProject.IsColor(color));
    public string ColorHex { get => (string)GetValue(ColorHexProperty); set => SetValue(ColorHexProperty, value); }
    public event RoutedEventHandler? ColorChanged;
    private static int[] customColors = new int[16];

    public ColorPicker()
    {
        InitializeComponent();
        string[] colors =
        [
            "#101C22", "#FFFFFF", "#E4E8EF", "#20232A", "#3B82F6", "#A178FF",
            "#64F1C2", "#FDE047", "#FB923C", "#F87171", "#8EEDB5"
        ];
        foreach (string color in colors)
        {
            var button = new Button
            {
                Width = 34, Height = 34, Margin = new Thickness(3), Padding = new Thickness(0),
                Background = Brush(color), ToolTip = color, Tag = color
            };
            System.Windows.Automation.AutomationProperties.SetName(button, "Цвет " + color);
            button.Click += (_, _) => ColorHex = (string)button.Tag;
            Swatches.Children.Add(button);
        }
        var custom = new Button { Content = "+", Width = 34, Height = 34, Margin = new Thickness(3), Padding = new Thickness(0), FontSize = 22, ToolTip = "Другой цвет…" };
        custom.Click += CustomColor; Swatches.Children.Add(custom);
        Refresh();
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var picker = (ColorPicker)sender;
        if (picker.Swatch is null) return;
        picker.Refresh(); picker.ColorChanged?.Invoke(picker, new RoutedEventArgs());
    }
    private void Refresh()
    {
        Swatch.Background = Brush(ColorHex); ColorCode.Text = ColorHex.ToUpperInvariant();
        foreach (var button in Swatches.Children.OfType<Button>())
        {
            if (button.Tag is not string) continue;
            bool selected = string.Equals(button.Tag as string, ColorHex, StringComparison.OrdinalIgnoreCase);
            button.BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(49, 95, 255)) : new SolidColorBrush(Color.FromRgb(216, 225, 238));
            button.BorderThickness = new Thickness(selected ? 2 : 1);
            if (selected)
            {
                var fill = (Color)ColorConverter.ConvertFromString(ColorHex);
                bool bright = (fill.R * 299 + fill.G * 587 + fill.B * 114) / 1000 > 160;
                button.Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0,5 L 4,9 L 11,0"), Stroke = bright ? Brushes.RoyalBlue : Brushes.White, StrokeThickness = 1.7, Width = 12, Height = 10 };
            }
            else button.Content = null;
        }
    }
    private void CustomColor(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.ColorDialog { FullOpen = true, Color = System.Drawing.ColorTranslator.FromHtml(ColorHex), CustomColors = customColors };
        var window = Window.GetWindow(this);
        var result = window is null ? dialog.ShowDialog() : dialog.ShowDialog(new DialogOwner(new System.Windows.Interop.WindowInteropHelper(window).Handle));
        if (result == Forms.DialogResult.OK)
        {
            ColorHex = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
            customColors = dialog.CustomColors;
        }
    }
    private sealed class DialogOwner(IntPtr handle) : Forms.IWin32Window { public IntPtr Handle => handle; }
}
