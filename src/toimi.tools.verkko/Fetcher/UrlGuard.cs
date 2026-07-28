using System.Net;
using System.Net.Sockets;

namespace toimi.tools.verkko.Fetcher;

/// <summary>
/// SSRF guard for outbound fetches: rejects hosts that are loopback, private,
/// link-local, CGNAT, or otherwise not externally routable. The IP-range logic
/// mirrors ruutu's ScribanRenderer.SafeUrl checks, adapted for the fetcher
/// (http is allowed here; scheme policy lives in FetchUrlTool).
/// </summary>
public static class UrlGuard
{
  public static bool IsBlockedHost(string host)
  {
    return Toimi.Core.Net.PrivateAddress.IsBlockedHost(host);
  }

  public static bool IsPrivate(IPAddress ip)
  {
    return Toimi.Core.Net.PrivateAddress.IsPrivate(ip);
  }

  /// <summary>
  /// SocketsHttpHandler.ConnectCallback that resolves the target host and refuses
  /// to connect to private/internal addresses. Runs for every connection the
  /// HttpClient opens — including redirect targets — so a public URL cannot
  /// redirect the fetcher into the cluster or local network.
  /// </summary>
  public static async ValueTask<Stream> GuardedConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
  {
    var host = context.DnsEndPoint.Host;
    var addresses = IPAddress.TryParse(host, out var literal)
      ? [literal]
      : await Dns.GetHostAddressesAsync(host, ct);

    var routable = addresses.Where(ip => !IsPrivate(ip)).ToArray();
    if (routable.Length == 0)
    {
      throw new HttpRequestException($"Blocked: '{host}' resolves to a private or internal address.");
    }

    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
    try
    {
      await socket.ConnectAsync(routable, context.DnsEndPoint.Port, ct);
      return new NetworkStream(socket, ownsSocket: true);
    }
    catch
    {
      socket.Dispose();
      throw;
    }
  }
}
