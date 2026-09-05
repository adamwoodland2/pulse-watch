using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ConnectionChecker.Models;

public enum CheckType
{
    Icmp,
    Tcp
}

public enum HostStatus
{
    Unknown,
    Online,
    Offline
}

public class HostEntry : INotifyPropertyChanged
{
    private string _name = "";
    private string _address = "";
    private CheckType _checkType = CheckType.Icmp;
    private int _port = 443;
    private int _intervalSeconds = 30;
    private bool _playSound = true;
    private bool _enabled = true;
    private int _retryCount = 1;
    private int _timeoutMs = 4000;
    private string? _lastFailure;
    private HostStatus _status = HostStatus.Unknown;
    private long _latencyMs = -1;
    private DateTime? _lastChecked;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public string Address
    {
        get => _address;
        set { _address = value; OnPropertyChanged(); }
    }

    public CheckType CheckType
    {
        get => _checkType;
        set { _checkType = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModeDisplay)); }
    }

    public int Port
    {
        get => _port;
        set { _port = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModeDisplay)); }
    }

    public int IntervalSeconds
    {
        get => _intervalSeconds;
        set { _intervalSeconds = value; OnPropertyChanged(); }
    }

    public bool PlaySound
    {
        get => _playSound;
        set { _playSound = value; OnPropertyChanged(); }
    }

    /// <summary>Unticked = target is paused: no checks, no alerts.</summary>
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusDisplay)); }
    }

    /// <summary>Extra attempts after a failed check before declaring offline. 0 = alert on first failure.</summary>
    public int RetryCount
    {
        get => _retryCount;
        set { _retryCount = value; OnPropertyChanged(); }
    }

    /// <summary>Per-check timeout in milliseconds (ICMP reply / TCP connect).</summary>
    public int TimeoutMs
    {
        get => _timeoutMs;
        set { _timeoutMs = value; OnPropertyChanged(); }
    }

    /// <summary>Why the last check failed (TIMEOUT, REFUSED, DNS, ...); null when up.</summary>
    [JsonIgnore]
    public string? LastFailure
    {
        get => _lastFailure;
        set { _lastFailure = value; OnPropertyChanged(); OnPropertyChanged(nameof(LatencyDisplay)); }
    }

    /// <summary>Per-host tile colour override (hex); null = use the global setting.</summary>
    public string? OfflineColor { get; set; }

    /// <summary>Per-host tile colour override (hex); null = use the global setting.</summary>
    public string? OnlineColor { get; set; }

    [JsonIgnore]
    public HostStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusDisplay)); }
    }

    [JsonIgnore]
    public long LatencyMs
    {
        get => _latencyMs;
        set { _latencyMs = value; OnPropertyChanged(); OnPropertyChanged(nameof(LatencyDisplay)); }
    }

    [JsonIgnore]
    public DateTime? LastChecked
    {
        get => _lastChecked;
        set { _lastChecked = value; OnPropertyChanged(); OnPropertyChanged(nameof(LastCheckedDisplay)); }
    }

    [JsonIgnore]
    public string ModeDisplay => CheckType == CheckType.Icmp ? "ICMP" : $"TCP:{Port}";

    [JsonIgnore]
    public string StatusDisplay => !Enabled ? "PAUSED" : Status switch
    {
        HostStatus.Online => "ONLINE",
        HostStatus.Offline => "OFFLINE",
        _ => "PENDING"
    };

    [JsonIgnore]
    public string LatencyDisplay =>
        Status == HostStatus.Online && LatencyMs >= 0 ? $"{LatencyMs} ms"
        : Status == HostStatus.Offline && LastFailure != null ? LastFailure
        : "—";

    [JsonIgnore]
    public string LastCheckedDisplay => LastChecked?.ToString("HH:mm:ss") ?? "—";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
