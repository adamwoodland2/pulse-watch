using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ConnectionChecker.Models;

namespace ConnectionChecker.Services;

public class StatusChangedEventArgs : EventArgs
{
    public required HostEntry Host { get; init; }
    public required HostStatus OldStatus { get; init; }
    public required HostStatus NewStatus { get; init; }

    /// <summary>Identifies the loop that raised this; lets subscribers drop stale events via IsCurrent.</summary>
    public required CancellationToken Token { get; init; }
}

/// <summary>
/// Runs one async check loop per host. Raises StatusChanged (marshalled by the
/// subscriber to the UI thread) only on genuine transitions.
/// </summary>
public class MonitorService : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _loops = new();

    public event EventHandler<StatusChangedEventArgs>? StatusChanged;

    public void Start(HostEntry host)
    {
        lock (_gate)
        {
            Stop(host.Id);
            var cts = new CancellationTokenSource();
            _loops[host.Id] = cts;
            _ = RunLoopAsync(host, cts.Token);
        }
    }

    // Note: Cancel only, no Dispose — the loop still holds the token and may
    // touch it (linked CTS, Task.Delay) after cancellation.
    public void Stop(Guid hostId)
    {
        lock (_gate)
        {
            if (_loops.Remove(hostId, out var cts))
                cts.Cancel();
        }
    }

    public void StopAll()
    {
        lock (_gate)
        {
            foreach (var cts in _loops.Values)
                cts.Cancel();
            _loops.Clear();
        }
    }

    /// <summary>True while the given token belongs to the host's currently registered loop.</summary>
    public bool IsCurrent(Guid hostId, CancellationToken token)
    {
        lock (_gate)
            return _loops.TryGetValue(hostId, out var cts) && cts.Token == token;
    }

    private async Task RunLoopAsync(HostEntry host, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var (isUp, latency, failure) = await CheckAsync(host, token);

            // Failed: retry before declaring offline. A success flips back
            // online immediately, so no retries on recovery.
            for (int attempt = 0; !isUp && attempt < host.RetryCount; attempt++)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                (isUp, latency, failure) = await CheckAsync(host, token);
            }
            // Publish only while this loop is still the registered one, so a
            // stopped/edited/removed host isn't mutated or alerted afterwards.
            if (!IsCurrent(host.Id, token)) return;

            var oldStatus = host.Status;
            var newStatus = isUp ? HostStatus.Online : HostStatus.Offline;

            host.LatencyMs = latency;
            host.LastChecked = DateTime.Now;
            host.LastFailure = isUp ? null : failure;
            host.Status = newStatus;

            if (newStatus != oldStatus)
            {
                StatusChanged?.Invoke(this, new StatusChangedEventArgs
                {
                    Host = host,
                    OldStatus = oldStatus,
                    NewStatus = newStatus,
                    Token = token
                });
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, host.IntervalSeconds)), token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private static async Task<(bool isUp, long latencyMs, string? failure)> CheckAsync(HostEntry host, CancellationToken token)
    {
        var timeoutMs = Math.Clamp(host.TimeoutMs, 100, 60000);
        try
        {
            // Tokens like {gateway} re-resolve every check, so switching
            // networks is picked up automatically.
            var address = NetworkTokens.Resolve(host.Address);
            if (address == null) return (false, -1, "NO NET");

            return host.CheckType == CheckType.Icmp
                ? await PingAsync(address, timeoutMs, token)
                : await TcpAsync(address, host.Port, timeoutMs, token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return (false, -1, "TIMEOUT"); // TCP connect exceeded the per-host timeout
        }
        catch (PingException)
        {
            return (false, -1, "DNS"); // ping couldn't resolve the name
        }
        catch (SocketException ex)
        {
            return (false, -1, ex.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData => "DNS",
                SocketError.ConnectionRefused => "REFUSED",
                SocketError.ConnectionReset => "RESET",
                SocketError.NetworkUnreachable or SocketError.HostUnreachable => "UNREACH",
                SocketError.TimedOut => "TIMEOUT",
                _ => "FAIL"
            });
        }
        catch
        {
            return (false, -1, "FAIL");
        }
    }

    private static async Task<(bool, long, string?)> PingAsync(string address, int timeoutMs, CancellationToken token)
    {
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken: token);
        return reply.Status switch
        {
            IPStatus.Success => (true, reply.RoundtripTime, null),
            IPStatus.TimedOut => (false, -1, "TIMEOUT"),
            IPStatus.DestinationHostUnreachable or
            IPStatus.DestinationNetworkUnreachable or
            IPStatus.DestinationUnreachable => (false, -1, "UNREACH"),
            IPStatus.TimeExceeded or IPStatus.TtlExpired => (false, -1, "TTL"),
            _ => (false, -1, "FAIL")
        };
    }

    // Happy-eyeballs-style connect: multi-homed hosts (CDNs) can have one dead
    // address cached first in DNS; trying only that one burns the whole timeout
    // even though the host is fine. Race up to 4 candidates (IPv6/IPv4
    // interleaved, 300 ms stagger) and take the first that connects.
    private static async Task<(bool, long, string?)> TcpAsync(string address, int port, int timeoutMs, CancellationToken token)
    {
        // The timeout budget covers DNS resolution too, so a slow resolver
        // can't stretch a check past the configured per-host timeout.
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        raceCts.CancelAfter(timeoutMs);

        var addresses = System.Net.IPAddress.TryParse(address, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(address, raceCts.Token);

        var candidates = Interleave(
                addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6),
                addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
            .Take(4).ToArray();
        if (candidates.Length == 0) return (false, -1, "DNS");

        var attempts = candidates
            .Select((addr, i) => AttemptConnectAsync(addr, port, i * 300, raceCts.Token))
            .ToList();

        Exception? lastRealError = null;
        while (attempts.Count > 0)
        {
            var done = await Task.WhenAny(attempts);
            attempts.Remove(done);
            try
            {
                var latency = await done;
                raceCts.Cancel();
                foreach (var loser in attempts)
                    _ = loser.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return (true, latency, null);
            }
            catch (OperationCanceledException)
            {
                // budget exhausted or race already won — keep draining
            }
            catch (Exception ex)
            {
                lastRealError = ex;
            }
        }

        if (lastRealError != null) throw lastRealError; // classified by CheckAsync
        throw new OperationCanceledException(); // every attempt timed out
    }

    private static async Task<long> AttemptConnectAsync(System.Net.IPAddress addr, int port, int delayMs, CancellationToken token)
    {
        if (delayMs > 0) await Task.Delay(delayMs, token);
        using var client = new TcpClient(addr.AddressFamily);
        var sw = Stopwatch.StartNew();
        await client.ConnectAsync(addr, port, token);
        return sw.ElapsedMilliseconds;
    }

    private static IEnumerable<T> Interleave<T>(IEnumerable<T> first, IEnumerable<T> second)
    {
        using var a = first.GetEnumerator();
        using var b = second.GetEnumerator();
        bool hasA = a.MoveNext(), hasB = b.MoveNext();
        while (hasA || hasB)
        {
            if (hasA) { yield return a.Current; hasA = a.MoveNext(); }
            if (hasB) { yield return b.Current; hasB = b.MoveNext(); }
        }
    }

    public void Dispose() => StopAll();
}
