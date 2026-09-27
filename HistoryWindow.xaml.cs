using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Windows;
using ConnectionChecker.Services;

namespace ConnectionChecker;

/// <summary>Live view of the outage history; one instance, owned by the main window.</summary>
public partial class HistoryWindow : Window
{
    private readonly HistoryLog _log;

    public HistoryWindow(HistoryLog log)
    {
        InitializeComponent();
        Icon = AppIcon.WindowIcon;
        _log = log;
        EventList.ItemsSource = log.Events;
        log.Events.CollectionChanged += OnEventsChanged;
        Closed += (_, _) => log.Events.CollectionChanged -= OnEventsChanged;
        Refresh();
    }

    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var count = _log.Events.Count;
        var offline = _log.Events.Count(x => x.Event == "OFFLINE");
        SummaryText.Text = $"Since the app started at {_log.Started:HH:mm, d MMM yyyy}  ·  " +
                           (count == 0 ? "no events" : $"{count} event{(count == 1 ? "" : "s")}, {offline} offline");
        EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.IsEnabled = count > 0;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export history",
            Filter = "CSV file (*.csv)|*.csv",
            DefaultExt = ".csv",
            FileName = $"PulseWatch-history-{DateTime.Now:yyyy-MM-dd-HHmm}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            // UTF-8 with BOM so Excel reads non-ASCII target names correctly.
            File.WriteAllText(dialog.FileName, _log.ToCsv(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show(this, $"Couldn't save the file:\n{ex.Message}", "PULSE//WATCH",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
