using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ConnectionChecker.Models;

namespace ConnectionChecker.Services;

public class StatusChangedEventArgs : EventArgs
{
    public required HostEntry Host { get; init; }
    public required HostStatus OldStatus { get; init; }
    public required HostStatus NewStatus { get; init; }
}

/// <summary>
/// Runs one async check loop per host. Raises StatusChanged (marshalled by the
/// subscriber to the UI thread) only on genuine transitions.
/// </summary>
public class MonitorService : IDisposable
{
    private const int CheckTimeoutMs = 4000;

    private readonly Dictionary<Guid, CancellationTokenSource> _loops = new();

    public event EventHandler<StatusChangedEventArgs>? StatusChanged;

    public void Start(HostEntry host)
    {
        Stop(host.Id);
        var cts = new CancellationTokenSource();
        _loops[host.Id] = cts;
        _ = RunLoopAsync(host, cts.Token);
    }

    public void Stop(Guid hostId)
    {
        if (_loops.Remove(hostId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public void StopAll()
    {
        foreach (var cts in _loops.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _loops.Clear();
    }

    private async Task RunLoopAsync(HostEntry host, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var (isUp, latency) = await CheckAsync(host, token);

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
                (isUp, latency) = await CheckAsync(host, token);
            }
            if (token.IsCancellationRequested) return;

            var oldStatus = host.Status;
            var newStatus = isUp ? HostStatus.Online : HostStatus.Offline;

            host.LatencyMs = latency;
            host.LastChecked = DateTime.Now;
            host.Status = newStatus;

            if (newStatus != oldStatus)
            {
                StatusChanged?.Invoke(this, new StatusChangedEventArgs
                {
                    Host = host,
                    OldStatus = oldStatus,
                    NewStatus = newStatus
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

    private static async Task<(bool isUp, long latencyMs)> CheckAsync(HostEntry host, CancellationToken token)
    {
        try
        {
            // Tokens like {gateway} re-resolve every check, so switching
            // networks is picked up automatically.
            var address = NetworkTokens.Resolve(host.Address);
            if (address == null) return (false, -1);

            return host.CheckType == CheckType.Icmp
                ? await PingAsync(address, token)
                : await TcpAsync(address, host.Port, token);
        }
        catch
        {
            return (false, -1);
        }
    }

    private static async Task<(bool, long)> PingAsync(string address, CancellationToken token)
    {
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(CheckTimeoutMs), cancellationToken: token);
        return reply.Status == IPStatus.Success ? (true, reply.RoundtripTime) : (false, -1);
    }

    private static async Task<(bool, long)> TcpAsync(string address, int port, CancellationToken token)
    {
        using var client = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(CheckTimeoutMs);

        var sw = Stopwatch.StartNew();
        await client.ConnectAsync(address, port, timeoutCts.Token);
        sw.Stop();
        return (true, sw.ElapsedMilliseconds);
    }

    public void Dispose() => StopAll();
}
