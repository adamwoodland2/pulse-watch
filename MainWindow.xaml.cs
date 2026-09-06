using System.Windows;
using System.Windows.Input;
using System.Collections.ObjectModel;
using ConnectionChecker.Models;
using ConnectionChecker.Services;
using WinForms = System.Windows.Forms;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDrop = System.Windows.DragDrop;
using DragDropEffects = System.Windows.DragDropEffects;

namespace ConnectionChecker;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<HostEntry> _hosts = new();
    private readonly MonitorService _monitor = new();
    private readonly AlertOverlayWindow _overlay = new();
    private readonly WinForms.NotifyIcon _trayIcon;
    private AppSettings _settings = new();
    private bool _exiting;
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();

        Icon = AppIcon.WindowIcon;

        _settings = SettingsService.Load(out var loadWarning);
        ApplyTileColors();
        _overlay.Muted = _settings.MuteSounds;

        // Subscribe before any loop starts so a fast first check can't
        // publish its initial offline transition into the void.
        _monitor.StatusChanged += OnStatusChanged;

        foreach (var host in _settings.Hosts)
        {
            _hosts.Add(host);
            if (host.Enabled)
                _monitor.Start(host);
        }

        if (loadWarning != null)
        {
            Loaded += (_, _) => System.Windows.MessageBox.Show(this, loadWarning,
                "PULSE//WATCH", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        HostList.ItemsSource = _hosts;
        HostList.SelectionChanged += (_, _) =>
        {
            var hasSelection = HostList.SelectedItem != null;
            EditButton.IsEnabled = hasSelection;
            RemoveButton.IsEnabled = hasSelection;
        };
        _hosts.CollectionChanged += (_, _) => UpdateEmptyState();
        UpdateEmptyState();

        _trayIcon = CreateTrayIcon();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
                HideToTray();
        };

        _overlay.Show(); // not tied to Loaded: must run even when starting minimized

        Closing += (_, e) =>
        {
            // X minimises to tray when enabled; only the tray's Exit really quits.
            if (_settings.CloseToTray && !_exitRequested)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            if (_settings.ConfirmOnExit)
            {
                // Owner only when visible — exiting from the tray has a hidden window.
                var answer = IsVisible
                    ? System.Windows.MessageBox.Show(this,
                        "Stop monitoring and exit PULSE//WATCH?", "PULSE//WATCH",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                    : System.Windows.MessageBox.Show(
                        "Stop monitoring and exit PULSE//WATCH?", "PULSE//WATCH",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    _exitRequested = false;
                    return;
                }
            }

            _exiting = true;
            _monitor.Dispose();
            SaveSettings();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _overlay.Close();
        };
    }

    // ===== Tray =====

    private WinForms.NotifyIcon CreateTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());

        var mute = new WinForms.ToolStripMenuItem("Mute alert sounds")
        {
            CheckOnClick = true,
            Checked = _settings.MuteSounds
        };
        mute.CheckedChanged += (_, _) =>
        {
            _settings.MuteSounds = mute.Checked;
            _overlay.Muted = mute.Checked;
            SaveSettings();
        };
        menu.Items.Add(mute);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _exitRequested = true; Close(); });

        var icon = new WinForms.NotifyIcon
        {
            Icon = AppIcon.CreateTrayIcon(),
            Text = "PULSE//WATCH — connection monitor",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => RestoreFromTray();
        return icon;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void HideToTray()
    {
        Hide(); // to tray; the overlay keeps showing alerts
        _trayIcon.ShowBalloonTip(2000, "PULSE//WATCH",
            "Still monitoring — alerts pop on the right edge of the screen.",
            WinForms.ToolTipIcon.Info);
    }

    private void ApplyTileColors()
    {
        if (ValidationHelpers.ParseColor(_settings.OfflineTileColor) is { } offline)
            _overlay.OfflineColor = offline;
        if (ValidationHelpers.ParseColor(_settings.OnlineTileColor) is { } online)
            _overlay.OnlineColor = online;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            ApplyTileColors();
            SaveSettings();
        }
    }

    // ===== State =====

    private void UpdateEmptyState()
        => EmptyState.Visibility = _hosts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void SaveSettings()
    {
        _settings.Hosts = _hosts.ToList();
        if (!SettingsService.Save(_settings))
        {
            _trayIcon?.ShowBalloonTip(3000, "PULSE//WATCH",
                "Settings could not be saved — recent changes may be lost on exit.",
                WinForms.ToolTipIcon.Warning);
        }
    }

    // ===== CRUD =====

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new EditHostWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            _hosts.Add(dialog.Result);
            if (dialog.Result.Enabled)
                _monitor.Start(dialog.Result);
            SaveSettings();
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => EditSelected();

    private static System.Windows.Controls.ListBoxItem? FindListBoxItem(object? source)
    {
        var element = source as DependencyObject;
        while (element != null && element is not System.Windows.Controls.ListBoxItem)
        {
            element = element is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return element as System.Windows.Controls.ListBoxItem;
    }

    // Double-click a row = edit that row; double-click empty space = add.
    // (Selection alone can't tell these apart — a selected row stays selected
    // when you click the background.)
    private void HostList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindListBoxItem(e.OriginalSource) is { DataContext: HostEntry host })
        {
            HostList.SelectedItem = host;
            EditSelected();
        }
        else
        {
            Add_Click(sender, e);
        }
    }

    // ===== Drag-to-reorder =====

    private Point _dragStart;
    private HostEntry? _dragCandidate;

    private void HostList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCandidate = FindListBoxItem(e.OriginalSource)?.DataContext as HostEntry;
    }

    private void HostList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;

        // Don't hijack plain clicks/double-clicks: require a real drag distance.
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var dragged = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(HostList, dragged, DragDropEffects.Move);
    }

    private void HostList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(HostEntry)) is not HostEntry dragged) return;
        var oldIndex = _hosts.IndexOf(dragged);
        if (oldIndex < 0) return;

        int insertAt;
        var targetItem = FindListBoxItem(e.OriginalSource);
        if (targetItem?.DataContext is HostEntry target && !ReferenceEquals(target, dragged))
        {
            // Above the row's midpoint = before it, below = after it.
            insertAt = _hosts.IndexOf(target);
            if (e.GetPosition(targetItem).Y > targetItem.ActualHeight / 2) insertAt++;
            if (insertAt > oldIndex) insertAt--; // account for removal of the dragged row
        }
        else
        {
            insertAt = _hosts.Count - 1; // dropped on empty space: move to end
        }

        if (insertAt != oldIndex)
        {
            _hosts.Move(oldIndex, insertAt);
            SaveSettings();
        }
    }

    private void EditSelected()
    {
        if (HostList.SelectedItem is not HostEntry host) return;

        var dialog = new EditHostWindow(_settings, host) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            _monitor.Stop(host.Id); // stop the old loop before mutating shared state
            host.Name = dialog.Result.Name;
            host.Address = dialog.Result.Address;
            host.CheckType = dialog.Result.CheckType;
            host.Port = dialog.Result.Port;
            host.IntervalSeconds = dialog.Result.IntervalSeconds;
            host.RetryCount = dialog.Result.RetryCount;
            host.TimeoutMs = dialog.Result.TimeoutMs;
            host.PlaySound = dialog.Result.PlaySound;
            host.OfflineColor = dialog.Result.OfflineColor;
            host.OnlineColor = dialog.Result.OnlineColor;
            host.Enabled = dialog.Result.Enabled;
            host.Status = HostStatus.Unknown;
            host.LatencyMs = -1;
            host.LastFailure = null;
            if (host.Enabled)
            {
                _monitor.Start(host); // fresh loop with new parameters
            }
            else
            {
                _overlay.DismissTilesFor(host.Id);
            }
            SaveSettings();
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (HostList.SelectedItem is not HostEntry host) return;

        _monitor.Stop(host.Id);
        _hosts.Remove(host);
        _overlay.DismissTilesFor(host.Id);
        SaveSettings();
    }

    private void ConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(SettingsService.Dir);
        System.Diagnostics.Process.Start("explorer.exe", SettingsService.Dir);
    }

    // ===== Alerts =====

    private void OnStatusChanged(object? sender, StatusChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_exiting) return;

            // Drop events from loops that were stopped/replaced (host removed,
            // edited, or paused) between publication and this marshal.
            if (!_monitor.IsCurrent(e.Host.Id, e.Token)) return;

            if (e.NewStatus == HostStatus.Offline)
            {
                _overlay.ShowAlert(e.Host, isOnline: false, _settings.AlertDurationSeconds);
            }
            else if (e.NewStatus == HostStatus.Online && e.OldStatus == HostStatus.Offline)
            {
                _overlay.DismissTilesFor(e.Host.Id);
                _overlay.ShowAlert(e.Host, isOnline: true, _settings.AlertDurationSeconds);
            }
        });
    }
}
