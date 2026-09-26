using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
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
            // DNS checks probe the server; Address is the name they look up.
            var address = NetworkTokens.Resolve(host.CheckType == CheckType.Dns ? host.DnsServer : host.Address, host.IpVersion);
            if (address == null)
                return (false, -1, host.IpVersion == IpVersion.Auto ? "NO NET" : NoFamilyCode(host.IpVersion));

            return host.CheckType switch
            {
                CheckType.Icmp => await PingAsync(address, host.IpVersion, timeoutMs, token),
                CheckType.Http or CheckType.Https => await HttpAsync(host, address, timeoutMs, token),
                CheckType.Dns => await DnsAsync(host, address, timeoutMs, token),
                _ => await TcpAsync(host, address, timeoutMs, token)
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return (false, -1, "TIMEOUT"); // no TCP connect / HTTP response / DNS answer within the per-host timeout
        }
        catch (NoFamilyException ex)
        {
            return (false, -1, NoFamilyCode(ex.Version));
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
        // One budget covers everything (DNS included), so a slow resolver or a
        // dead cached address can't stretch a check past the per-host timeout.
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        budgetCts.CancelAfter(timeoutMs);

        var (socket, latency) = await ConnectAsync(host, address, host.Port, timeoutMs, budgetCts.Token, token);
        socket.Dispose();
        return (true, latency, null);
    }

    // GET on a fresh connection per check (no pooling, so every check proves
    // the whole path, like the TCP probe). Up = any status below 400; the body
    // is never read. Redirects are not followed: a 3xx means this server
    // answered, and following could end up measuring a different host.
    private async Task<(bool, long, string?)> HttpAsync(HostEntry host, string address, int timeoutMs, CancellationToken token)
    {
        var certRejected = false;

        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            // Connect through the same sticky-route / race logic as TCP checks,
            // so IP version, fast fail-over and RST-on-close all apply here too.
            ConnectCallback = async (_, ct) =>
            {
                var (socket, _) = await ConnectAsync(host, address, host.Port, timeoutMs, ct, token);
                return new NetworkStream(socket, ownsSocket: true);
            },
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, _, _, errors) =>
                {
                    if (errors == SslPolicyErrors.None || host.IgnoreCertErrors) return true;
                    certRejected = true;
                    return false;
                }
            }
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        // The URI host only feeds the Host header, SNI and certificate name
        // matching; the connection itself goes to `address` via ConnectCallback.
        var uriHost = IPAddress.TryParse(address, out var ip) &&
                      ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0
            ? new IPAddress(ip.GetAddressBytes()).ToString() // drop the %scope, which a URI can't carry
            : address;
        // Appended as text, not resolved relative to the base: a path such as
        // "//other.example/x" would otherwise become a new authority and send
        // Host/SNI/certificate checks to a different name.
        var authority = new UriBuilder(host.CheckType == CheckType.Https ? "https" : "http", uriHost, host.Port)
            .Uri.GetLeftPart(UriPartial.Authority);
        var path = host.Path.StartsWith('/') ? host.Path : "/" + host.Path;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(authority + path));
        request.Headers.ConnectionClose = true;
        request.Headers.UserAgent.ParseAdd(UserAgent);

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        budgetCts.CancelAfter(timeoutMs);

        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budgetCts.Token);
            var code = (int)response.StatusCode;
            return code < 400 ? (true, sw.ElapsedMilliseconds, null) : (false, -1, $"HTTP {code}");
        }
        catch (HttpRequestException ex)
        {
            if (certRejected) return (false, -1, "CERT");
            // Connect failures arrive wrapped; unwrap so CheckAsync classifies
            // them exactly like a TCP check (REFUSED, UNREACH, NO V6, ...).
            if (ex.InnerException is SocketException or NoFamilyException or OperationCanceledException)
                ExceptionDispatchInfo.Throw(ex.InnerException);
            return (false, -1, ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "DNS",
                HttpRequestError.SecureConnectionError => "TLS",
                _ => "FAIL"
            });
        }
    }

    // Sends our own query straight to the target server (no OS resolver or
    // cache in the way) and passes only if the requested record type comes
    // back. Catches "online but DNS broken", which ICMP/TCP:53 can't see.
    private static async Task<(bool, long, string?)> DnsAsync(HostEntry host, string serverAddress, int timeoutMs, CancellationToken token)
    {
        // Settings are validated on load and in the dialog; this makes sure a
        // name that slipped past both never goes on the wire malformed.
        var name = ValidationHelpers.NormalizeDnsName(host.Address);
        if (name == null) return (false, -1, "BAD NAME");

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        budgetCts.CancelAfter(timeoutMs);

        // The server itself: a literal or token is used as-is; a name (e.g.
        // dns.google) goes through the OS resolver, first address of the family.
        if (!IPAddress.TryParse(serverAddress, out var server))
        {
            var addresses = FilterFamily(await Dns.GetHostAddressesAsync(serverAddress, budgetCts.Token), host.IpVersion);
            server = addresses.FirstOrDefault() ?? throw (host.IpVersion == IpVersion.Auto
                ? new SocketException((int)SocketError.HostNotFound)
                : new NoFamilyException(host.IpVersion));
        }
        else if (FilterFamily(new[] { server }, host.IpVersion).Length == 0)
        {
            throw new NoFamilyException(host.IpVersion);
        }

        var qtype = host.DnsRecordType == DnsRecordType.AAAA ? DnsTypeAAAA : DnsTypeA;
        // Unpredictable ID: together with the echoed-question check, an
        // off-path spoofer can't easily forge a reply that passes.
        var id = (ushort)RandomNumberGenerator.GetInt32(0x10000);
        var query = BuildDnsQuery(id, name, qtype);
        var endpoint = new IPEndPoint(server, host.Port);
        var sw = Stopwatch.StartNew();

        var (outcome, failure) = await DnsOverUdpAsync(endpoint, query, id, name, qtype, budgetCts.Token);
        if (outcome == DnsOutcome.Truncated)
        {
            // TC only means "didn't fit in UDP, ask again over TCP"; it proves
            // nothing about the answer, so repeat the query over TCP.
            (outcome, failure) = await DnsOverTcpAsync(endpoint, query, id, name, qtype, budgetCts.Token);
            if (outcome != DnsOutcome.Answered) failure = "BADREPLY"; // TCP replies must be complete and ours
        }
        return failure == null ? (true, sw.ElapsedMilliseconds, null) : (false, -1, failure);
    }

    private const int DnsResendMs = 1000;
    private const ushort DnsTypeA = 1, DnsTypeCname = 5, DnsTypeAAAA = 28, DnsClassIn = 1;

    private enum DnsOutcome { NotOurs, Truncated, Answered }

    private static async Task<(DnsOutcome, string?)> DnsOverUdpAsync(
        IPEndPoint endpoint, byte[] query, ushort id, string name, ushort qtype, CancellationToken budget)
    {
        var buffer = new byte[4096];
        using var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        // Connecting a UDP socket sends nothing; it pins the peer so only its
        // replies are received, and a closed port surfaces as ConnectionReset.
        socket.Connect(endpoint);
        try
        {
            while (true)
            {
                await socket.SendAsync(query, SocketFlags.None, budget);

                // UDP can lose the query or the answer: resend every second
                // until the budget runs out, like dig/nslookup do.
                using var resendCts = CancellationTokenSource.CreateLinkedTokenSource(budget);
                resendCts.CancelAfter(DnsResendMs);
                try
                {
                    while (true)
                    {
                        var n = await socket.ReceiveAsync(buffer, SocketFlags.None, resendCts.Token);
                        var result = ParseDnsResponse(buffer.AsSpan(0, n), id, name, qtype);
                        if (result.outcome != DnsOutcome.NotOurs) return result;
                    }
                }
                catch (OperationCanceledException) when (!budget.IsCancellationRequested)
                {
                    // no answer yet — send again
                }
            }
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            return (DnsOutcome.Answered, "REFUSED"); // ICMP port unreachable: host is up, nothing listening
        }
    }

    // RFC 1035 4.2.2: same message, prefixed with a two-byte length.
    private static async Task<(DnsOutcome, string?)> DnsOverTcpAsync(
        IPEndPoint endpoint, byte[] query, ushort id, string name, ushort qtype, CancellationToken budget)
    {
        using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.LingerState = new LingerOption(enable: true, seconds: 0); // RST on close, as for TCP probes
        await socket.ConnectAsync(endpoint, budget);
        await using var stream = new NetworkStream(socket, ownsSocket: false);

        var framed = new byte[query.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
        query.CopyTo(framed, 2);
        await stream.WriteAsync(framed, budget);

        var lengthPrefix = new byte[2];
        await stream.ReadExactlyAsync(lengthPrefix, budget);
        var reply = new byte[BinaryPrimitives.ReadUInt16BigEndian(lengthPrefix)];
        await stream.ReadExactlyAsync(reply, budget);
        return ParseDnsResponse(reply, id, name, qtype);
    }

    private static byte[] BuildDnsQuery(ushort id, string name, ushort qtype)
    {
        var q = new List<byte>(18 + name.Length)
        {
            (byte)(id >> 8), (byte)id,
            0x01, 0x00, // standard query, recursion desired
            0, 1, 0, 0, 0, 0, 0, 0 // one question, no other records
        };
        foreach (var label in name.Split('.'))
        {
            q.Add((byte)label.Length);
            q.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        q.AddRange(new byte[] { 0, (byte)(qtype >> 8), (byte)qtype, 0, (byte)DnsClassIn }); // root, QTYPE, QCLASS
        return q.ToArray();
    }

    /// <summary>
    /// NotOurs: not a reply to this exact query (wrong ID, not a standard
    /// response, or a different question) — ignored. Truncated: retry over
    /// TCP. Answered: failure is null only when a record of the requested
    /// type, class IN and correct size is owned by the name asked for or by
    /// the end of its CNAME chain.
    /// </summary>
    private static (DnsOutcome outcome, string? failure) ParseDnsResponse(ReadOnlySpan<byte> r, ushort id, string name, ushort qtype)
    {
        if (r.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(r) != id) return (DnsOutcome.NotOurs, null);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(r[2..]);
        if ((flags & 0x8000) == 0 || (flags & 0x7800) != 0) return (DnsOutcome.NotOurs, null); // QR = response, OPCODE = QUERY

        try
        {
            // The question must come back exactly as asked.
            if (BinaryPrimitives.ReadUInt16BigEndian(r[4..]) != 1) return (DnsOutcome.NotOurs, null);
            var pos = ReadDnsName(r, 12, out var echoed);
            if (!echoed.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                BinaryPrimitives.ReadUInt16BigEndian(r[pos..]) != qtype ||
                BinaryPrimitives.ReadUInt16BigEndian(r[(pos + 2)..]) != DnsClassIn)
                return (DnsOutcome.NotOurs, null);
            pos += 4;

            var rcode = flags & 0x000F;
            if (rcode != 0)
                return (DnsOutcome.Answered, rcode switch { 2 => "SERVFAIL", 3 => "NXDOMAIN", 5 => "REFUSED", _ => $"RCODE {rcode}" });
            if ((flags & 0x0200) != 0)
                return (DnsOutcome.Truncated, null);

            var records = new List<(string owner, ushort type, int rdPos, int rdLength)>();
            int answers = BinaryPrimitives.ReadUInt16BigEndian(r[6..]);
            for (var i = 0; i < answers; i++)
            {
                pos = ReadDnsName(r, pos, out var owner);
                var type = BinaryPrimitives.ReadUInt16BigEndian(r[pos..]);
                var cls = BinaryPrimitives.ReadUInt16BigEndian(r[(pos + 2)..]);
                var rdLength = BinaryPrimitives.ReadUInt16BigEndian(r[(pos + 8)..]);
                var rdPos = pos + 10;
                if (rdPos + rdLength > r.Length) return (DnsOutcome.Answered, "BADREPLY");
                if (cls == DnsClassIn) records.Add((owner, type, rdPos, rdLength));
                pos = rdPos + rdLength;
            }

            // Follow CNAMEs from the name asked for; an unrelated record
            // elsewhere in the answer doesn't count.
            var expectedLength = qtype == DnsTypeAAAA ? 16 : 4;
            var target = name;
            for (var hop = 0; hop < 16; hop++)
            {
                if (records.Exists(x => x.type == qtype && x.rdLength == expectedLength &&
                                        x.owner.Equals(target, StringComparison.OrdinalIgnoreCase)))
                    return (DnsOutcome.Answered, null);
                var cname = records.Find(x => x.type == DnsTypeCname &&
                                              x.owner.Equals(target, StringComparison.OrdinalIgnoreCase));
                if (cname.owner == null) break;
                ReadDnsName(r, cname.rdPos, out target);
            }
            return (DnsOutcome.Answered, "NODATA"); // name exists but has no record of this type
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or FormatException)
        {
            return (DnsOutcome.Answered, "BADREPLY"); // ours by ID, but malformed
        }
    }

    /// <summary>Decodes a (possibly compressed) name at pos; returns the position after it in the message.</summary>
    private static int ReadDnsName(ReadOnlySpan<byte> r, int pos, out string name)
    {
        var sb = new System.Text.StringBuilder();
        var end = -1;
        var jumps = 0;
        while (true)
        {
            var len = r[pos];
            if (len == 0)
            {
                if (end < 0) end = pos + 1;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (++jumps > 32) throw new FormatException("DNS name compression loop");
                if (end < 0) end = pos + 2; // the name ends at the first pointer
                pos = ((len & 0x3F) << 8) | r[pos + 1];
                continue;
            }
            if ((len & 0xC0) != 0) throw new FormatException("Unsupported DNS label type");
            if (sb.Length > 0) sb.Append('.');
            sb.Append(System.Text.Encoding.ASCII.GetString(r.Slice(pos + 1, len)));
            if (sb.Length > 253) throw new FormatException("DNS name too long");
            pos += 1 + len;
        }
        name = sb.ToString();
        return end;
    }

    private static readonly string UserAgent =
        $"PulseWatch/{typeof(MonitorService).Assembly.GetName().Version?.ToString(3) ?? "1"}";

    /// <summary>Forced IP version, but the target has no address of that family.</summary>
    private sealed class NoFamilyException(IpVersion version) : Exception
    {
        public IpVersion Version { get; } = version;
    }

    /// <summary>
    /// Opens a TCP connection to the host: the sticky route while it still
    /// works, otherwise resolve and race candidates. The caller owns the
    /// returned socket. <paramref name="budget"/> bounds the connect;
    /// <paramref name="token"/> is the loop's own token.
    /// </summary>
    private async Task<(Socket socket, long latencyMs)> ConnectAsync(
        HostEntry host, string address, int port, int timeoutMs, CancellationToken budget, CancellationToken token)
    {
        // Fast path: single socket to the address that worked last time.
        // Capped at half the budget so a newly-dead address still leaves the
        // full race time to find a live one below.
        if (_routes.TryGetValue(host.Id, out var route) &&
            route.Address == address &&
            route.Version == host.IpVersion &&
            DateTime.UtcNow - route.ResolvedAt < RouteTtl)
        {
            using var quickCts = CancellationTokenSource.CreateLinkedTokenSource(budget);
            quickCts.CancelAfter(Math.Max(500, timeoutMs / 2));
            try
            {
                return await AttemptConnectAsync(route.Ip, port, quickCts.Token);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                _routes.TryRemove(host.Id, out _); // cached address went bad — fall through to the race
            }
        }

        // Full path: resolve and race up to 4 candidates (IPv6/IPv4
        // interleaved, staggered as below). Multi-homed hosts (CDNs) can have one
        // dead address listed first in DNS; trying only that one would burn
        // the whole timeout even though the host is fine.
        var addresses = System.Net.IPAddress.TryParse(address, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(address, budget);
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

        // Forced family: only that family's addresses (a literal of the other
        // family, or a name with no such record, reports NO V4 / NO V6).
        addresses = FilterFamily(addresses, host.IpVersion);
        if (addresses.Length == 0) throw new NoFamilyException(host.IpVersion);

        var candidates = Interleave(
                addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6),
                addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
            .Take(4).ToArray();

        // Own token for the race: cancelling the losers must not cancel what
        // the caller does next with the winning socket (the HTTP request).
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(budget);
        var attempts = new List<Task<(Socket socket, long latencyMs)>>();
        var next = 0;

        // Happy Eyeballs (RFC 8305 section 5): each attempt gets a head start
        // of ConnectStaggerMs before the next candidate joins, but a failed
        // attempt hands over at once. A fixed per-slot delay would make a dead
        // family (e.g. IPv6 with no route, which fails instantly) cost the
        // full stagger on every race.
        void StartNext() => attempts.Add(AttemptConnectAsync(candidates[next++], port, raceCts.Token));
        StartNext();

        Exception? lastRealError = null;
        while (attempts.Count > 0)
        {
            var waitOn = new List<Task>(attempts);
            if (next < candidates.Length && !raceCts.IsCancellationRequested)
                waitOn.Add(Task.Delay(ConnectStaggerMs, raceCts.Token));
            var done = await Task.WhenAny(waitOn);

            if (done is not Task<(Socket socket, long latencyMs)> attempt)
            {
                if (!done.IsCanceled) StartNext(); // head start used up, still no winner
                continue;
            }

            attempts.Remove(attempt);
            try
            {
                var (socket, latency) = await attempt;
                raceCts.Cancel();
                // A loser can finish connecting before it sees the cancel;
                // close those sockets rather than leave them to the GC.
                foreach (var loser in attempts)
                    _ = loser.ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully) t.Result.socket.Dispose();
                        else _ = t.Exception;
                    }, TaskScheduler.Default);
                _routes[host.Id] = new CachedRoute(address, host.IpVersion,
                    ((IPEndPoint)socket.RemoteEndPoint!).Address, DateTime.UtcNow);
                return (socket, latency);
            }
            catch (OperationCanceledException)
            {
                // budget exhausted — keep draining
            }
            catch (Exception ex)
            {
                lastRealError = ex;
                if (next < candidates.Length) StartNext(); // failed fast: don't sit out the head start
            }
        }

        if (lastRealError != null) throw lastRealError; // classified by CheckAsync
        throw new OperationCanceledException(); // every attempt timed out
    }

    private const int ConnectStaggerMs = 300;

    private static async Task<(Socket socket, long latencyMs)> AttemptConnectAsync(IPAddress addr, int port, CancellationToken token)
    {
        // Abortive close (RST) instead of FIN: probe sockets exchange no data,
        // and a graceful close would leave one TIME_WAIT/FIN_WAIT entry per
        // check per raced address until the OS times them out. HTTP checks
        // close the same way once the response headers are in. Raw Socket, not
        // TcpClient: TcpClient.Dispose calls Shutdown first, which sends a FIN
        // and defeats the linger-0 abort.
        var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.LingerState = new LingerOption(enable: true, seconds: 0);
            var sw = Stopwatch.StartNew();
            await socket.ConnectAsync(addr, port, token);
            return (socket, sw.ElapsedMilliseconds);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
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
