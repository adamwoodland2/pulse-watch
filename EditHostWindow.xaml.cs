using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ConnectionChecker.Models;
using Border = System.Windows.Controls.Border;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;

namespace ConnectionChecker;

public partial class EditHostWindow : Window
{
    public HostEntry? Result { get; private set; }

    private readonly string _globalOffline;
    private readonly string _globalOnline;

    public EditHostWindow(Services.AppSettings settings)
    {
        InitializeComponent();
        Icon = AppIcon.WindowIcon;
        _globalOffline = settings.OfflineTileColor;
        _globalOnline = settings.OnlineTileColor;
        ValidationHelpers.MakeNumeric(PortBox);
        ValidationHelpers.MakeNumeric(IntervalBox);
        ValidationHelpers.MakeNumeric(RetryBox);
        ValidationHelpers.MakeNumeric(TimeoutBox);
        RefreshSwatches();
    }

    public EditHostWindow(Services.AppSettings settings, HostEntry existing) : this(settings)
    {
        HeaderText.Text = "EDIT TARGET";
        Title = "Edit target";
        NameBox.Text = existing.Name;
        AddressBox.Text = existing.Address;
        TypeCombo.SelectedIndex = existing.CheckType == CheckType.Tcp ? 0 : 1;
        PortBox.Text = existing.Port.ToString();
        IntervalBox.Text = existing.IntervalSeconds.ToString();
        RetryBox.Text = existing.RetryCount.ToString();
        TimeoutBox.Text = existing.TimeoutMs.ToString();
        ActiveCheck.IsChecked = existing.Enabled;
        SoundCheck.IsChecked = existing.PlaySound;
        OfflineColorBox.Text = existing.OfflineColor ?? "";
        OnlineColorBox.Text = existing.OnlineColor ?? "";
    }

    private void ColorBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshSwatches();

    private void RefreshSwatches()
    {
        if (OfflineSwatch == null || OnlineSwatch == null) return; // during InitializeComponent
        UpdateSwatch(OfflineColorBox, OfflineSwatch, _globalOffline);
        UpdateSwatch(OnlineColorBox, OnlineSwatch, _globalOnline);
    }

    // Blank override shows the current global colour, so the swatch always
    // previews what the tile will actually look like.
    private static void UpdateSwatch(TextBox box, Border swatch, string fallback)
    {
        var text = box.Text.Trim().Length > 0 ? box.Text : fallback;
        var color = ValidationHelpers.ParseColor(text);
        swatch.Background = color is Color c ? new SolidColorBrush(c) : Brushes.Transparent;
    }

    private void OfflineSwatch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => PickInto(OfflineColorBox, _globalOffline);

    private void OnlineSwatch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => PickInto(OnlineColorBox, _globalOnline);

    private static void PickInto(TextBox box, string fallbackSeed)
    {
        var seed = box.Text.Trim().Length > 0 ? box.Text : fallbackSeed;
        if (ValidationHelpers.PickColor(seed) is string hex)
            box.Text = hex;
    }

    private void TypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PortPanel != null)
            // Hidden (not Collapsed) keeps the space reserved so nothing shifts.
            PortPanel.Visibility = TypeCombo.SelectedIndex == 0 ? Visibility.Visible : Visibility.Hidden;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var address = ValidationHelpers.NormalizeHost(AddressBox.Text);
        if (address == null)
        {
            ShowError("Enter a valid host name or IP address (no spaces or special characters).");
            return;
        }
        AddressBox.Text = address; // show the normalized form (e.g. URL -> host)

        var isTcp = TypeCombo.SelectedIndex == 0;
        int port = 0;
        if (isTcp && (!int.TryParse(PortBox.Text, out port) || port < 1 || port > 65535))
        {
            ShowError("Port must be a number between 1 and 65535.");
            return;
        }

        if (!int.TryParse(IntervalBox.Text, out var interval) || interval < 1 || interval > 86400)
        {
            ShowError("Interval must be between 1 and 86400 seconds.");
            return;
        }

        if (!int.TryParse(RetryBox.Text, out var retries) || retries < 0 || retries > 10)
        {
            ShowError("Retries must be between 0 and 10 (0 alerts on the first failure).");
            return;
        }

        if (!int.TryParse(TimeoutBox.Text, out var timeout) || timeout < 100 || timeout > 60000)
        {
            ShowError("Timeout must be between 100 and 60000 milliseconds.");
            return;
        }

        var offlineColor = OfflineColorBox.Text.Trim();
        var onlineColor = OnlineColorBox.Text.Trim();
        if ((offlineColor.Length > 0 && ValidationHelpers.ParseColor(offlineColor) == null) ||
            (onlineColor.Length > 0 && ValidationHelpers.ParseColor(onlineColor) == null))
        {
            ShowError("Tile colours must be valid hex values (e.g. #FF8C00) or left blank for the default.");
            return;
        }

        var name = NameBox.Text.Trim();
        Result = new HostEntry
        {
            Name = name.Length > 0 ? name : address,
            Address = address,
            CheckType = isTcp ? CheckType.Tcp : CheckType.Icmp,
            Port = isTcp ? port : 443,
            IntervalSeconds = interval,
            RetryCount = retries,
            TimeoutMs = timeout,
            Enabled = ActiveCheck.IsChecked == true,
            PlaySound = SoundCheck.IsChecked == true,
            OfflineColor = offlineColor.Length > 0 ? offlineColor : null,
            OnlineColor = onlineColor.Length > 0 ? onlineColor : null
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
