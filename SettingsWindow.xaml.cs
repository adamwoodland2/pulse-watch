using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ConnectionChecker.Services;
using Brushes = System.Windows.Media.Brushes;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;

namespace ConnectionChecker;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Icon = AppIcon.WindowIcon;
        _settings = settings;

        ValidationHelpers.MakeNumeric(DurationBox);
        CloseToTrayCheck.IsChecked = settings.CloseToTray;
        ConfirmExitCheck.IsChecked = settings.ConfirmOnExit;
        AutoStartCheck.IsChecked = StartupService.IsEnabled();
        DurationBox.Text = settings.AlertDurationSeconds.ToString();
        OfflineColorBox.Text = settings.OfflineTileColor;
        OnlineColorBox.Text = settings.OnlineTileColor;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"PULSE//WATCH v{version?.ToString(3) ?? "1.0.0"} — connection monitor";
    }

    private void ColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (OfflineSwatch == null || OnlineSwatch == null) return; // during InitializeComponent
        UpdateSwatch(OfflineColorBox, OfflineSwatch);
        UpdateSwatch(OnlineColorBox, OnlineSwatch);
    }

    private static void UpdateSwatch(TextBox box, Border swatch)
    {
        var color = ValidationHelpers.ParseColor(box.Text);
        swatch.Background = color is Color c ? new SolidColorBrush(c) : Brushes.Transparent;
    }

    private void OfflineSwatch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ValidationHelpers.PickColor(OfflineColorBox.Text) is string hex)
            OfflineColorBox.Text = hex;
    }

    private void OnlineSwatch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ValidationHelpers.PickColor(OnlineColorBox.Text) is string hex)
            OnlineColorBox.Text = hex;
    }

    private void ResetColors_Click(object sender, RoutedEventArgs e)
    {
        OfflineColorBox.Text = AppSettings.DefaultOfflineColor;
        OnlineColorBox.Text = AppSettings.DefaultOnlineColor;
    }

    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://github.com/adamwoodland2/pulse-watch",
            UseShellExecute = true
        });
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DurationBox.Text, out var secs) || secs < 1 || secs > 3600)
        {
            System.Windows.MessageBox.Show(this, "Alert timeout must be between 1 and 3600 seconds.",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ValidationHelpers.ParseColor(OfflineColorBox.Text) == null ||
            ValidationHelpers.ParseColor(OnlineColorBox.Text) == null)
        {
            System.Windows.MessageBox.Show(this, "Colours must be valid hex values, e.g. #FF3B5C.",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var wantAutoStart = AutoStartCheck.IsChecked == true;
        if (wantAutoStart != StartupService.IsEnabled() && !StartupService.SetEnabled(wantAutoStart))
        {
            System.Windows.MessageBox.Show(this,
                "Couldn't update the auto-start entry in the registry. Other settings were still saved.",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _settings.CloseToTray = CloseToTrayCheck.IsChecked == true;
        _settings.ConfirmOnExit = ConfirmExitCheck.IsChecked == true;
        _settings.AlertDurationSeconds = secs;
        _settings.OfflineTileColor = OfflineColorBox.Text.Trim();
        _settings.OnlineTileColor = OnlineColorBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
