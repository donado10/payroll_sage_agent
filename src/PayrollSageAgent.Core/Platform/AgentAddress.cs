using System.Net;
using System.Net.Sockets;

namespace PayrollSageAgent.Core.Platform;

/// <summary>
/// Which address an agent may follow after pairing. The contract says: the address the platform returns
/// (<c>PairResponse.endpoint</c>) is the one used from then on — and it may legitimately differ from the
/// one IT typed (a dedicated agents' hostname). What it may NOT do is point INWARD from outside:
///
///   - back at this server (<c>localhost</c>) when the platform was reached elsewhere — a platform whose
///     agent address is misconfigured, or one from before the fix of 2026-10-17: every call goes nowhere;
///   - at an INTERNAL address (private, link-local, a single-label or <c>.local</c> name) when the platform
///     was reached at a public one — over the WAN, that address means a machine on the CUSTOMER's network,
///     and the agent would hand its bearer token to whatever answers there.
///
/// A LAN agent following a LAN address, or any agent following a public one, is fine. No name is
/// resolved: the rule reads the address as written.
/// </summary>
public static class AgentAddress
{
    private static readonly string[] InternalSuffixes = [".local", ".lan", ".internal", ".home.arpa", ".localdomain"];

    public static bool IsThisMachine(Uri uri) => uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    public static bool IsInternal(Uri uri)
    {
        if (IsThisMachine(uri)) return true;
        var host = uri.Host.Trim('[', ']');
        if (IPAddress.TryParse(host, out var ip)) return IsInternal(ip);
        return !host.Contains('.', StringComparison.Ordinal)
            || InternalSuffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether an agent that reached the platform at <paramref name="calledAt"/> may call <paramref name="endpoint"/> from now on.</summary>
    public static bool MayFollow(Uri calledAt, Uri endpoint) =>
        (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps)
        && !(IsThisMachine(endpoint) && !IsThisMachine(calledAt))
        && !(IsInternal(endpoint) && !IsInternal(calledAt));

    private static bool IsInternal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        }
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // carrier-grade NAT: inside an operator's network
    }
}
