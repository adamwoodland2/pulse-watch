using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ConnectionChecker.Models;
using Border = System.Windows.Controls.Border;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace ConnectionChecker;

public partial class EditHostWindow : Window
{
    public HostEntry? Result { get; private set; }

    /// <summary>True when the dialog closed via REMOVE (edit mode only); Result is null then.</summary>
    public bool RemoveRequested { get; private set; }

    private readonly string _globalOffline;
    private readonly string _globalOnline;
    private readonly DispatcherTimer _removeArmTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public EditHostWindow(Services.AppSettings settings)
    {
        InitializeComponent();
        Icon = AppIcon.WindowIcon;
        _globalOffline = settings.OfflineTileColor;
        _globalOnline = settings.OnlineTileColor;
        _removeArmTimer.Tick += (_, _) => DisarmRemove();
        SourceInitialized += (_, _) => MaxHeight = WorkArea().Height;
        Loaded += (_, _) => KeepOnScreen();
        SizeChanged += (_, _) => KeepOnScreen(); // switching method grows/shrinks the form
        RefreshSwatches(); // NumberBox controls enforce digits-only themselves
        ApplyMethod(CheckType.Tcp);
    }

    public EditHostWindow(Services.AppSettings settings, HostEntry existing) : this(settings)
    {
        HeaderText.Text = "EDIT TARGET";
        Title = "Edit target";
        RemoveButton.Visibility = Visibility.Visible;
        NameBox.Text = existing.Name;
        AddressBox.Text = existing.Address;
        RadioFor(existing.CheckType).IsChecked = true; // before the port, which it may default
        PortBox.Text = existing.Port.ToString();
        PathBox.Text = existing.Path;
        IgnoreCertCheck.IsChecked = existing.IgnoreCertErrors;
        DnsServerBox.Text = existing.DnsServer;
        RecordCombo.SelectedIndex = (int)existing.DnsRecordType;
        IpVersionCombo.SelectedIndex = (int)existing.IpVersion;
        IntervalBox.Text = existing.IntervalSeconds.ToString();
        RetryBox.Text = existing.RetryCount.ToString();
        TimeoutBox.Text = existing.TimeoutMs.ToString();
        ActiveCheck.IsChecked = existing.Enabled;
        SoundCheck.IsChecked = existing.PlaySound;
        OfflineColorBox.Text = existing.OfflineColor ?? "";
        OnlineColorBox.Text = existing.OnlineColor ?? "";
    }

    // ===== Screen fit =====
    // SizeToContent + NoResize would otherwise let a tall form (HTTPS/DNS
    // options, high DPI, 768px screens) push SAVE off the bottom of the screen.
    // MaxHeight caps the window; the form then scrolls above a pinned footer.

    /// <summary>Work area of the monitor the owner (or this dialog) is on, in DIPs.</summary>
    private Rect WorkArea()
    {
        var handle = new WindowInteropHelper(Owner ?? this).Handle;
        var px = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                         ?? System.Windows.Media.Matrix.Identity;
        return new Rect(fromDevice.Transform(new System.Windows.Point(px.Left, px.Top)),
                        fromDevice.Transform(new System.Windows.Point(px.Right, px.Bottom)));
    }

    private void KeepOnScreen()
    {
        if (!IsLoaded) return;
        var area = WorkArea();
        if (Top + ActualHeight > area.Bottom) Top = area.Bottom - ActualHeight;
        if (Top < area.Top) Top = area.Top;
    }

    // Hairline above the footer while the form is cut off, so it reads as scrollable.
    private void FormScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        => FooterBar.BorderThickness = new Thickness(0, FormScroller.ScrollableHeight > 0 ? 1 : 0, 0, 0);

    // ===== Tile colours =====

    private void ColorBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshSwatches();

    private void RefreshSwatches()
    {
        if (OfflineSwatch == null || OnlineSwatch == null) return; // during InitializeComponent
        UpdateSwatch(OfflineColorBox, OfflineSwatch, OfflinePlaceholder, _globalOffline);
        UpdateSwatch(OnlineColorBox, OnlineSwatch, OnlinePlaceholder, _globalOnline);
    }

    // Blank override shows the current global colour, so the swatch always
    // previews what the tile will actually look like.
    private static void UpdateSwatch(TextBox box, Border swatch, TextBlock placeholder, string fallback)
    {
        var blank = box.Text.Trim().Length == 0;
        placeholder.Visibility = blank ? Visibility.Visible : Visibility.Collapsed;
        var color = ValidationHelpers.ParseColor(blank ? fallback : box.Text);
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

    // ===== Check method =====

    private RadioButton RadioFor(CheckType type) => type switch
    {
        CheckType.Icmp => IcmpRadio,
        CheckType.Http => HttpRadio,
        CheckType.Https => HttpsRadio,
        CheckType.Dns => DnsRadio,
        _ => TcpRadio
    };

    private CheckType SelectedType =>
        IcmpRadio.IsChecked == true ? CheckType.Icmp
        : HttpRadio.IsChecked == true ? CheckType.Http
        : HttpsRadio.IsChecked == true ? CheckType.Https
        : DnsRadio.IsChecked == true ? CheckType.Dns
        : CheckType.Tcp;

    private static int? DefaultPort(CheckType type) => type switch
    {
        CheckType.Http => 80,
        CheckType.Https => 443,
        CheckType.Dns => 53,
        _ => null
    };

    private void Method_Checked(object sender, RoutedEventArgs e)
    {
        if (MethodHint == null) return; // during InitializeComponent
        var type = SelectedType;

        // Follow the method's standard port, unless a custom one was entered.
        if (DefaultPort(type) is int port && PortBox.Text is "80" or "443" or "53")
            PortBox.Text = port.ToString();

        ApplyMethod(type);
    }

    /// <summary>Shows only the options that apply to the method.</summary>
    private void ApplyMethod(CheckType type)
    {
        static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

        PortPanel.Visibility = Show(type != CheckType.Icmp);
        PathPanel.Visibility = Show(type is CheckType.Http or CheckType.Https);
        IgnoreCertCheck.Visibility = Show(type == CheckType.Https);
        DnsServerPanel.Visibility = Show(type == CheckType.Dns);
        RecordPanel.Visibility = Show(type == CheckType.Dns);

        // For DNS the top field is still "what's being checked": the name to resolve.
        AddressLabel.Text = type == CheckType.Dns ? "NAME TO LOOK UP" : "HOST / IP ADDRESS";
        AddressHint.Text = type == CheckType.Dns
            ? "The domain to resolve, e.g. google.com  ·  pasted URLs are fine"
            : "{gateway} = default gateway  ·  {dns} = DNS server  ·  pasted URLs are fine";

        MethodHint.Text = type switch
        {
            CheckType.Icmp => "Up when an ICMP echo reply comes back.",
            CheckType.Http or CheckType.Https => "Up on any response below 400. Redirects are not followed.",
            CheckType.Dns => "Asks this server directly (no Windows cache). Up when it returns this record type.",
            _ => "Up when a TCP connection to the port opens. No data is sent."
        };
    }

    // ===== Remove (two clicks, so a stray click can't delete a target) =====

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_removeArmTimer.IsEnabled)
        {
            _removeArmTimer.Stop();
            RemoveRequested = true;
            DialogResult = true;
            return;
        }
        RemoveButton.Content = "CLICK AGAIN TO REMOVE";
        RemoveButton.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x3B, 0x5C));
        _removeArmTimer.Start();
    }

    private void DisarmRemove()
    {
        _removeArmTimer.Stop();
        RemoveButton.Content = "REMOVE";
        RemoveButton.ClearValue(BackgroundProperty);
    }

    // ===== Save =====

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // HTTP(S) with a pasted URL: take scheme, port and path from it too.
        if (SelectedType is CheckType.Http or CheckType.Https &&
            Uri.TryCreate(AddressBox.Text.Trim(), UriKind.Absolute, out var url) &&
            (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
        {
            RadioFor(url.Scheme == Uri.UriSchemeHttps ? CheckType.Https : CheckType.Http).IsChecked = true;
            PortBox.Text = url.Port.ToString();
            PathBox.Text = url.PathAndQuery;
        }

        var type = SelectedType;
        var isDns = type == CheckType.Dns;
        var address = ValidationHelpers.NormalizeHost(AddressBox.Text);
        if (isDns && address != null)
            address = ValidationHelpers.NormalizeDnsName(address); // a real domain, not an IP or {token}
        if (address == null)
        {
            ShowError(isDns
                ? "Enter a domain name to look up, e.g. google.com."
                : "Enter a valid host name or IP address (no spaces or special characters).");
            return;
        }
        AddressBox.Text = address; // show the normalized form (e.g. URL -> host)

        var usesPort = type != CheckType.Icmp;
        int port = 0;
        if (usesPort && (!int.TryParse(PortBox.Text, out port) || port < 1 || port > 65535))
        {
            ShowError("Port must be a number between 1 and 65535.");
            return;
        }

        var isHttp = type is CheckType.Http or CheckType.Https;
        var path = ValidationHelpers.NormalizePath(PathBox.Text);
        if (isHttp && path == null)
        {
            ShowError("Path must not contain spaces (e.g. / or /health?full=1).");
            return;
        }
        if (isHttp) PathBox.Text = path;

        var dnsServer = ValidationHelpers.NormalizeHost(DnsServerBox.Text);
        if (isDns && dnsServer == null)
        {
            ShowError("Enter the DNS server to ask: an IP address, a host name, {dns} or {gateway}.");
            return;
        }
        if (isDns) DnsServerBox.Text = dnsServer;

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
            CheckType = type,
            Port = usesPort ? port : 443,
            Path = isHttp ? path! : "/",
            IgnoreCertErrors = type == CheckType.Https && IgnoreCertCheck.IsChecked == true,
            DnsServer = isDns ? dnsServer! : "{dns}",
            DnsRecordType = isDns ? (DnsRecordType)Math.Clamp(RecordCombo.SelectedIndex, 0, 1) : DnsRecordType.A,
            IpVersion = (IpVersion)Math.Clamp(IpVersionCombo.SelectedIndex, 0, 2),
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

    protected override void OnClosed(EventArgs e)
    {
        _removeArmTimer.Stop();
        base.OnClosed(e);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
