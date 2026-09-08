using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using ConnectionChecker.Models;
using ConnectionChecker.Services;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Cursors = System.Windows.Input.Cursors;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace ConnectionChecker;

/// <summary>
/// Borderless, transparent, topmost strip pinned to the right edge of the
/// primary screen. Hosts the alert tiles so they appear even when the main
/// window is minimised or hidden to the tray.
/// </summary>
public partial class AlertOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newStyle);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    // Live offline tiles per host, so a recovery can dismiss them early.
    private readonly Dictionary<Guid, List<Border>> _offlineTiles = new();

    // Tile accents; MainWindow overwrites these from settings.
    public Color OfflineColor { get; set; } = Color.FromRgb(0xFF, 0x3B, 0x5C);
    public Color OnlineColor { get; set; } = Color.FromRgb(0x22, 0xE5, 0x84);

    /// <summary>Global mute (tray toggle): tiles still show, pings don't play.</summary>
    public bool Muted { get; set; }

    public AlertOverlayWindow()
    {
        InitializeComponent();
        PositionOnScreen();
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea))
                PositionOnScreen();
        };
        SourceInitialized += (_, _) =>
        {
            // Never steal focus, never show in Alt+Tab.
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, GWL_EXSTYLE,
                GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            PositionOnScreen(); // re-run with the window's real device transform available
        };
    }

    private void PositionOnScreen()
    {
        Width = 380;

        // --monitor N picks a specific screen (1-based); out of range -> primary.
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (App.MonitorOverride is int n && n >= 1 && n <= screens.Length)
        {
            // Screen gives device pixels; WPF wants DIPs. Prefer this window's
            // actual device transform (correct sign/scale for monitors left of
            // or above the primary); fall back to desktop DPI pre-Show.
            var wa = screens[n - 1].WorkingArea;
            double scale = PresentationSource.FromVisual(this)?.CompositionTarget
                ?.TransformFromDevice.M11 ?? GetDipScale();
            Height = wa.Height * scale;
            Left = (wa.Right * scale) - Width;
            Top = wa.Top * scale;
            return;
        }

        var area = SystemParameters.WorkArea;
        Height = area.Height;
        Left = area.Right - Width;
        Top = area.Top;
    }

    private static double GetDipScale()
    {
        using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        return 96.0 / g.DpiX;
    }

    public void ShowAlert(HostEntry host, bool isOnline, int durationSeconds)
    {
        // Topmost can be silently lost (another topmost window asserting itself,
        // Explorer restart, resume from sleep) — re-assert it for every tile.
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        if (host.PlaySound && !Muted)
        {
            if (isOnline) SoundService.PlayUp();
            else SoundService.PlayDown();
        }

        // Per-host override wins; fall back to the global colour.
        var accent = isOnline
            ? (host.OnlineColor is string on ? ValidationHelpers.ParseColor(on) : null) ?? OnlineColor
            : (host.OfflineColor is string off ? ValidationHelpers.ParseColor(off) : null) ?? OfflineColor;
        var accentBrush = new SolidColorBrush(accent);

        var tile = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x17, 0x26)),
            BorderBrush = accentBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 0, 0, 10),
            Cursor = Cursors.Hand,
            RenderTransform = new TranslateTransform(380, 0),
            Opacity = 0,
            Effect = new DropShadowEffect { Color = accent, BlurRadius = 20, ShadowDepth = 0, Opacity = 0.45 }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var stripe = new Rectangle { Fill = accentBrush, RadiusX = 2, RadiusY = 2 };
        Grid.SetColumn(stripe, 0);
        grid.Children.Add(stripe);

        var content = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        Grid.SetColumn(content, 1);

        content.Children.Add(new TextBlock
        {
            Text = isOnline ? "TARGET RESTORED" : "TARGET DOWN",
            Foreground = accentBrush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            FontWeight = FontWeights.Bold
        });
        content.Children.Add(new TextBlock
        {
            Text = host.Name,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xE2, 0xF0)),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        content.Children.Add(new TextBlock
        {
            Text = $"{host.Address}  ·  {host.ModeDisplay}  ·  {DateTime.Now:HH:mm:ss}",
            Foreground = new SolidColorBrush(Color.FromRgb(0x5E, 0x71, 0x91)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0)
        });

        grid.Children.Add(content);
        tile.Child = grid;

        tile.MouseLeftButtonUp += (_, _) => DismissTile(tile, host.Id);
        AlertHost.Children.Insert(0, tile);

        if (!isOnline)
        {
            if (!_offlineTiles.TryGetValue(host.Id, out var list))
                _offlineTiles[host.Id] = list = new List<Border>();
            list.Add(tile);
        }

        var slideIn = new DoubleAnimation(380, 0, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280));
        tile.RenderTransform.BeginAnimation(TranslateTransform.XProperty, slideIn);
        tile.BeginAnimation(OpacityProperty, fadeIn);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(durationSeconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            DismissTile(tile, host.Id);
        };
        timer.Start();
        tile.Unloaded += (_, _) => timer.Stop();
    }

    /// <summary>Remove any offline tiles for a host (recovery, or host removed).</summary>
    public void DismissTilesFor(Guid hostId)
    {
        if (_offlineTiles.Remove(hostId, out var tiles))
            foreach (var tile in tiles.ToList())
                DismissTile(tile);
    }

    private void DismissTile(Border tile, Guid? hostId = null)
    {
        if (!AlertHost.Children.Contains(tile)) return;

        if (hostId is Guid id && _offlineTiles.TryGetValue(id, out var list))
        {
            list.Remove(tile);
            if (list.Count == 0) _offlineTiles.Remove(id);
        }

        var slideOut = new DoubleAnimation(0, 380, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
        fadeOut.Completed += (_, _) => AlertHost.Children.Remove(tile);
        tile.RenderTransform.BeginAnimation(TranslateTransform.XProperty, slideOut);
        tile.BeginAnimation(OpacityProperty, fadeOut);
    }
}
