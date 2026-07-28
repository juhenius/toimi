using System.Net;
using System.Net.Sockets;

namespace Toimi.Core.Net;

/// <summary>
/// Canonical private/non-routable address policy shared by verkko's fetch SSRF
/// guard and ruutu's safe_url template filter. One copy so a new reserved range
/// added here protects both — the earlier hand-maintained copies had already drifted.
/// </summary>
public static class PrivateAddress
{
  public static bool IsBlockedHost(string host)
  {
    if (string.IsNullOrWhiteSpace(host))
    {
      return true;
    }
    if (IPAddress.TryParse(host, out var ip))
    {
      return IsPrivate(ip);
    }
    // Single-label hostname (router, localhost, cluster service) — not externally routable.
    return !host.Contains('.');
  }

  public static bool IsPrivate(IPAddress ip)
  {
    if (IPAddress.IsLoopback(ip))
    {
      return true;
    }

    if (ip.AddressFamily == AddressFamily.InterNetworkV6)
    {
      if (ip.IsIPv6LinkLocal)
      {
        return true;
      }
      var b6 = ip.GetAddressBytes();
      if ((b6[0] & 0xFE) == 0xFC)
      {
        return true; // fc00::/7 unique-local
      }
      if (ip.IsIPv4MappedToIPv6)
      {
        return IsPrivate(ip.MapToIPv4()); // unwrap ::ffff:a.b.c.d
      }
      if (b6.Take(12).All(b => b == 0))
      {
        // Deprecated IPv4-compatible form (::a.b.c.d) — treat as its embedded IPv4.
        return IsPrivate(new IPAddress(b6[12..]));
      }
      return false;
    }

    if (ip.AddressFamily == AddressFamily.InterNetwork)
    {
      var b = ip.GetAddressBytes();
      return b[0] == 0                                  // 0.0.0.0/8 (unspecified)
          || b[0] == 10                                 // 10/8
          || b[0] == 127                                // 127/8 loopback
          || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // 100.64/10 CGNAT (RFC 6598)
          || (b[0] == 169 && b[1] == 254)               // 169.254/16 link-local
          || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)  // 172.16/12
          || (b[0] == 192 && b[1] == 168);              // 192.168/16
    }

    return false;
  }
}
