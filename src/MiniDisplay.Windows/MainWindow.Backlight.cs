using System.Windows;
using System.Windows.Threading;

namespace MiniDisplay.Windows;

public partial class MainWindow
{
    private int desiredBrightness = 80;
    private bool brightnessConnected, brightnessSimulated, brightnessDirty;
    private readonly DispatcherTimer brightnessSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    private void InitializeBacklight()
    {
        desiredBrightness = Math.Clamp(LocalPreferences.Load().Brightness, 0, 100);
        BrightnessInput.Value = desiredBrightness;
        BrightnessValue.Text = desiredBrightness + "%";
        brightnessSaveTimer.Tick += (_, _) => PersistBrightness();
        UpdateBacklightState(null);
    }

    private void ChangeBrightness(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready) return;
        int value = Math.Clamp((int)Math.Round(BrightnessInput.Value), 0, 100);
        Volatile.Write(ref desiredBrightness, value);
        BrightnessValue.Text = value + "%";
        BrightnessStatus.Text = brightnessConnected ? "Применяем " + value + "%…" : "Применится после подключения дисплея или эмулятора.";
        brightnessDirty = true;
        brightnessSaveTimer.Stop(); brightnessSaveTimer.Start();
    }

    private void RestoreBacklight(object sender, RoutedEventArgs e) => BrightnessInput.Value = 100;

    private void PersistBrightness()
    {
        brightnessSaveTimer.Stop();
        if (!brightnessDirty) return;
        try
        {
            LocalPreferences.Update(preferences => preferences with { Brightness = Volatile.Read(ref desiredBrightness) });
            brightnessDirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { BrightnessStatus.Text = "Не удалось сохранить яркость: " + ex.Message; }
    }

    private void UpdateBacklightState(bool? supported, bool simulated = false)
    {
        brightnessConnected = supported == true;
        brightnessSimulated = simulated;
        BrightnessInput.IsEnabled = BacklightOnButton.IsEnabled = supported != false;
        BrightnessStatus.Text = supported is null ? "Применится после подключения дисплея или эмулятора."
            : supported == false ? "Регулировка недоступна в этой прошивке. Установите новую во вкладке «Настройки» и переподключитесь."
            : "Применяем " + Volatile.Read(ref desiredBrightness) + "%…";
    }

    private void BacklightApplied(int actual)
    {
        int requested = Volatile.Read(ref desiredBrightness);
        BrightnessStatus.Text = actual == requested ? (brightnessSimulated ? "Эмулятор · " : "Подсветка · ") + actual + "%" + (actual == 0 ? " · выключена" : "") : "Применяем " + requested + "%…";
        EmulatorBacklightShade.Visibility = brightnessSimulated ? Visibility.Visible : Visibility.Collapsed;
        EmulatorBacklightShade.Opacity = 1 - actual / 100d;
    }
}
