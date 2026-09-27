using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using ConnectionChecker.Models;

namespace ConnectionChecker.Services;

/// <summary>One outage edge. A snapshot, so it outlives edits to (or removal of) its target.</summary>
public sealed class HistoryEvent
{
    public required DateTime Time { get; init; }
    public required string Target { get; init; }
    public required string Type { get; init; }

    /// <summary>"OFFLINE" or "ONLINE".</summary>
    public required string Event { get; init; }

    /// <summary>Failure code for OFFLINE (TIMEOUT, REFUSED, HTTP 503, ...); empty for ONLINE.</summary>
    public required string Reason { get; init; }

    public string TimeDisplay => Time.ToString("yyyy-MM-dd  HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>
/// In-memory outage history since the app started: every OFFLINE (including a
/// target that is down on the first check) and every recovery from a logged
/// OFFLINE. A target that simply starts online logs nothing. Not persisted.
/// UI thread only (fed from MainWindow's marshalled StatusChanged handler).
/// </summary>
public sealed class HistoryLog
{
    // Enough for weeks of a flapping target; beyond it the oldest go first.
    private const int MaxEvents = 10_000;

    // Last state logged per target. Driven by this rather than the monitor's
    // old/new status, because editing a target resets it to Unknown: without
    // this, a target still down after an edit would log a second OFFLINE, and
    // one that recovered after an edit would never log its ONLINE.
    private readonly Dictionary<Guid, bool> _loggedOffline = new();

    public DateTime Started { get; } = DateTime.Now;

    /// <summary>Newest first, for binding.</summary>
    public ObservableCollection<HistoryEvent> Events { get; } = new();

    public void Record(HostEntry host, HostStatus newStatus)
    {
        var wasOffline = _loggedOffline.TryGetValue(host.Id, out var offline) && offline;

        if (newStatus == HostStatus.Offline && !wasOffline)
        {
            Add(host, "OFFLINE", host.LastFailure ?? "FAIL");
            _loggedOffline[host.Id] = true;
        }
        else if (newStatus == HostStatus.Online)
        {
            if (wasOffline) Add(host, "ONLINE", "");
            _loggedOffline[host.Id] = false;
        }
    }

    private void Add(HostEntry host, string evt, string reason)
    {
        Events.Insert(0, new HistoryEvent
        {
            Time = DateTime.Now,
            Target = host.Name,
            Type = host.ModeDisplay,
            Event = evt,
            Reason = reason
        });
        while (Events.Count > MaxEvents)
            Events.RemoveAt(Events.Count - 1);
    }

    /// <summary>Oldest first, one row per event: Date, Time, Target, Type, Event, Reason.</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder("Date,Time,Target,Type,Event,Reason\r\n");
        for (var i = Events.Count - 1; i >= 0; i--)
        {
            var e = Events[i];
            sb.Append(e.Time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
              .Append(e.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
              .Append(CsvField(e.Target)).Append(',')
              .Append(CsvField(e.Type)).Append(',')
              .Append(e.Event).Append(',')
              .Append(CsvField(e.Reason)).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string CsvField(string value)
    {
        // Target names are free text: stop a name like "=HYPERLINK(...)" from
        // running as a formula when the file is opened in Excel.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
