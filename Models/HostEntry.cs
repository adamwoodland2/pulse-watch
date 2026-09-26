using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ConnectionChecker.Models;

public enum CheckType
{
    Icmp,
    Tcp,
    Http,
    Https,
    Dns
}

public enum DnsRecordType
{
    A,
    AAAA
}

public enum IpVersion
{
    Auto,
    IPv4,
    IPv6
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
    private string _path = "/";
    private bool _ignoreCertErrors;
    private string _dnsServer = "{dns}";
    private DnsRecordType _dnsRecordType = DnsRecordType.A;
    private IpVersion _ipVersion = IpVersion.Auto;
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

    /// <summary>HTTP(S) only: path (and optional query) requested, always starting with '/'.</summary>
    public string Path
    {
        get => _path;
        set { _path = value; OnPropertyChanged(); }
    }

    /// <summary>HTTPS only: accept invalid/self-signed/expired certificates. Off by default.</summary>
    public bool IgnoreCertErrors
    {
        get => _ignoreCertErrors;
        set { _ignoreCertErrors = value; OnPropertyChanged(); }
    }

    /// <summary>DNS only: the server to ask (IP, host name, or {dns}/{gateway}); Address is the name looked up.</summary>
    public string DnsServer
    {
        get => _dnsServer;
        set { _dnsServer = value; OnPropertyChanged(); }
    }

    /// <summary>DNS only: record type that must come back for the check to pass.</summary>
    public DnsRecordType DnsRecordType
    {
        get => _dnsRecordType;
        set { _dnsRecordType = value; OnPropertyChanged(); }
    }

    /// <summary>Auto = race whatever resolves (IPv6 first); IPv4/IPv6 = that family only.</summary>
    public IpVersion IpVersion
    {
        get => _ipVersion;
        set { _ipVersion = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModeDisplay)); }
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

    /// <summary>Per-check timeout in milliseconds (ICMP reply / TCP connect / HTTP response headers / DNS answer).</summary>
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
    public string ModeDisplay =>
        CheckType switch
        {
            CheckType.Icmp => "ICMP",
            CheckType.Http => Port == 80 ? "HTTP" : $"HTTP:{Port}",
            CheckType.Https => Port == 443 ? "HTTPS" : $"HTTPS:{Port}",
            CheckType.Dns => Port == 53 ? "DNS" : $"DNS:{Port}",
            _ => $"TCP:{Port}"
        } +
        IpVersion switch { IpVersion.IPv4 => " v4", IpVersion.IPv6 => " v6", _ => "" };

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
