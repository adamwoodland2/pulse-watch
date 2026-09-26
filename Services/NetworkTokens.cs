using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ConnectionChecker.Models;

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

    /// <summary>
    /// Returns the concrete address to check, or null if a token has no address
    /// of the requested family right now. Auto prefers IPv4, falls back to IPv6.
    /// </summary>
    public static string? Resolve(string address, IpVersion version) => address.ToLowerInvariant() switch
    {
        "{gateway}" => Pick(p => p.GatewayAddresses.Select(g => g.Address), version),
        "{dns}" => Pick(p => p.DnsAddresses, version),
        _ => address
    };

    private static string? Pick(Func<IPInterfaceProperties, IEnumerable<IPAddress>> selector, IpVersion version)
    {
        try
        {
            var all = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetIPProperties())
                // Interfaces with a default gateway first (stable sort keeps
                // Windows' order otherwise): virtual adapters such as VMware
                // and WSL/Hyper-V have none, and must not supply {dns}.
                .OrderByDescending(p => p.GatewayAddresses.Any(g => !IsUnspecified(g.Address)))
                .SelectMany(selector)
                // fec0:0:0:ffff::1-3 are Windows' placeholder DNS servers on
                // adapters with no IPv6 DNS configured; site-local is deprecated
                // and never a real server.
                .Where(a => !IsUnspecified(a) && !a.IsIPv6SiteLocal)
                .ToList();

            var v4 = all.Where(a => a.AddressFamily == AddressFamily.InterNetwork);
            var v6 = all.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6);

            // IPv6 gateways are usually link-local (fe80::…%scope); ToString()
            // keeps the scope id, which Ping/Socket need to pick the interface.
            var chosen = version switch
            {
                IpVersion.IPv4 => v4.FirstOrDefault(),
                IpVersion.IPv6 => v6.FirstOrDefault(),
                _ => v4.FirstOrDefault() ?? v6.FirstOrDefault()
            };
            return chosen?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUnspecified(IPAddress a) => IPAddress.Any.Equals(a) || IPAddress.IPv6Any.Equals(a);
}
