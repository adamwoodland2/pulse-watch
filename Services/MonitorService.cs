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

    // Last address that worked per host: steady-state checks probe it with a
    // single socket instead of re-resolving DNS and racing 4 candidates every
    // check. With many targets that volume can exhaust home-router NAT tables
    // and trip SYN-flood protection, degrading all other traffic.
    private sealed record CachedRoute(string Address, IpVersion Version, IPAddress Ip, DateTime ResolvedAt);

    private static IPAddress[] FilterFamily(IPAddress[] addresses, IpVersion version) => version switch
    {
        IpVersion.IPv4 => addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray(),
        IpVersion.IPv6 => addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToArray(),
        _ => addresses
    };

    private static string NoFamilyCode(IpVersion version) => version == IpVersion.IPv6 ? "NO V6" : "NO V4";
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CachedRoute> _routes = new();
    private static readonly TimeSpan RouteTtl = TimeSpan.FromMinutes(5);

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
        try
        {
            await RunLoopCoreAsync(host, token);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown: Stop() can cancel mid-check (CheckAsync only
            // swallows cancellation while the loop token is alive), and a
            // closing dispatcher cancels StatusChanged marshalling. Without
            // this, the fire-and-forget loop task faults unobserved.
        }
    }

    private async Task RunLoopCoreAsync(HostEntry host, CancellationToken token)
    {
        // De-phase the loops: many targets starting together would fire a
        // burst of checks at t=0 and stay synchronized every interval,
        // hammering the router with dozens of simultaneous SYNs.
        var jitterMs = Random.Shared.Next(200, (int)Math.Min(host.IntervalSeconds * 1000L, 8000));
        try
        {
            await Task.Delay(jitterMs, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

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

    private async Task<(bool isUp, long latencyMs, string? failure)> CheckAsync(HostEntry host, CancellationToken token)
    {
        var timeoutMs = Math.Clamp(host.TimeoutMs, 100, 60000);
        try
        {
            // Tokens like {gateway} re-resolve every check, so switching
            // networks is picked up automatically.
            var address = NetworkTokens.Resolve(host.Address, host.IpVersion);
            if (address == null)
                return (false, -1, host.IpVersion == IpVersion.Auto ? "NO NET" : NoFamilyCode(host.IpVersion));

            return host.CheckType == CheckType.Icmp
                ? await PingAsync(address, host.IpVersion, timeoutMs, token)
                : await TcpAsync(host, address, timeoutMs, token);
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

    // Uses the synchronous Ping.Send on a pool thread, NOT SendPingAsync: on
    // Windows the async implementation leaks exactly one kernel handle per
    // call (reclaimed only by a GC, which this low-allocation app rarely
    // triggers), measured at ~1 handle/ping regardless of instance reuse.
    // Send() is bounded by Ping's own timeout, so the blocked thread is short-lived.
    private static async Task<(bool, long, string?)> PingAsync(string address, IpVersion version, int timeoutMs, CancellationToken token)
    {
        using var ping = new Ping();

        PingReply reply;
        if (version == IpVersion.Auto)
        {
            // Let the OS pick (usually IPv6 if there's an AAAA) — current behaviour.
            reply = await Task.Run(() => ping.Send(address, timeoutMs), token);
        }
        else
        {
            using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            dnsCts.CancelAfter(timeoutMs);
            var addresses = IPAddress.TryParse(address, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(address, dnsCts.Token);
            var candidates = FilterFamily(addresses, version);
            if (candidates.Length == 0) return (false, -1, NoFamilyCode(version));
            var target = candidates[0];
            reply = await Task.Run(() => ping.Send(target, timeoutMs), token);
        }

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

    private async Task<(bool, long, string?)> TcpAsync(HostEntry host, string address, int timeoutMs, CancellationToken token)
    {
        var port = host.Port;

        // One budget covers everything (DNS included), so a slow resolver or a
        // dead cached address can't stretch a check past the per-host timeout.
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        budgetCts.CancelAfter(timeoutMs);

        // Fast path: single socket to the address that worked last time.
        // Capped at half the budget so a newly-dead address still leaves the
        // full race time to find a live one below.
        if (_routes.TryGetValue(host.Id, out var route) &&
            route.Address == address &&
            route.Version == host.IpVersion &&
            DateTime.UtcNow - route.ResolvedAt < RouteTtl)
        {
            using var quickCts = CancellationTokenSource.CreateLinkedTokenSource(budgetCts.Token);
            quickCts.CancelAfter(Math.Max(500, timeoutMs / 2));
            try
            {
                var (latency, _) = await AttemptConnectAsync(route.Ip, port, 0, quickCts.Token);
                return (true, latency, null);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                _routes.TryRemove(host.Id, out _); // cached address went bad — fall through to the race
            }
        }

        // Full path: resolve and race up to 4 candidates (IPv6/IPv4
        // interleaved, 300 ms stagger). Multi-homed hosts (CDNs) can have one
        // dead address listed first in DNS; trying only that one would burn
        // the whole timeout even though the host is fine.
        var addresses = System.Net.IPAddress.TryParse(address, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(address, budgetCts.Token);
        if (addresses.Length == 0) return (false, -1, "DNS");

        // Forced family: only that family's addresses (a literal of the other
        // family, or a name with no such record, reports NO V4 / NO V6).
        addresses = FilterFamily(addresses, host.IpVersion);
        if (addresses.Length == 0) return (false, -1, NoFamilyCode(host.IpVersion));

        var candidates = Interleave(
                addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6),
                addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
            .Take(4).ToArray();

        var attempts = candidates
            .Select((addr, i) => AttemptConnectAsync(addr, port, i * 300, budgetCts.Token))
            .ToList();

        Exception? lastRealError = null;
        while (attempts.Count > 0)
        {
            var done = await Task.WhenAny(attempts);
            attempts.Remove(done);
            try
            {
                var (latency, winner) = await done;
                budgetCts.Cancel();
                foreach (var loser in attempts)
                    _ = loser.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                _routes[host.Id] = new CachedRoute(address, host.IpVersion, winner, DateTime.UtcNow);
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

    private static async Task<(long latencyMs, IPAddress addr)> AttemptConnectAsync(IPAddress addr, int port, int delayMs, CancellationToken token)
    {
        if (delayMs > 0) await Task.Delay(delayMs, token);
        // Abortive close (RST) instead of FIN: probe sockets exchange no data,
        // and a graceful close would leave one TIME_WAIT/FIN_WAIT entry per
        // check per raced address until the OS times them out. Raw Socket, not
        // TcpClient: TcpClient.Dispose calls Shutdown first, which sends a FIN
        // and defeats the linger-0 abort.
        using var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.LingerState = new LingerOption(enable: true, seconds: 0);
        var sw = Stopwatch.StartNew();
        await socket.ConnectAsync(addr, port, token);
        return (sw.ElapsedMilliseconds, addr);
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
