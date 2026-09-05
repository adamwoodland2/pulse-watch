using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ConnectionChecker.Services;

/// <summary>
/// Special address tokens that resolve to derived network values at check
/// time: {gateway} = default gateway, {dns} = first DNS server.
/// </summary>
public static class NetworkTokens
{
    public static readonly string[] All = { "{gateway}", "{dns}" };

    public static bool IsToken(string address)
        => All.Contains(address.ToLowerInvariant());

    /// <summary>Returns the concrete address to check, or null if a token can't resolve right now.</summary>
    public static string? Resolve(string address) => address.ToLowerInvariant() switch
    {
        "{gateway}" => FirstIPv4(p => p.GatewayAddresses.Select(g => g.Address)),
        "{dns}" => FirstIPv4(p => p.DnsAddresses),
        _ => address
    };

    private static string? FirstIPv4(Func<IPInterfaceProperties, IEnumerable<System.Net.IPAddress>> selector)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => selector(n.GetIPProperties()))
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork &&
                            !System.Net.IPAddress.Any.Equals(a))
                .Select(a => a.ToString())
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
