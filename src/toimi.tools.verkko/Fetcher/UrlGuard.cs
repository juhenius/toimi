using System.Net;
using System.Net.Sockets;
using Toimi.Core.Net;

namespace toimi.tools.verkko.Fetcher;

/// <summary>
/// SSRF guard for outbound fetches. The private/non-routable address policy is
/// the shared Toimi.Core.Net.PrivateAddress; this class applies it at connect
/// time. Scheme policy lives in FetchUrlTool (http is allowed here).
/// </summary>
public static class UrlGuard
{
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

    var routable = addresses.Where(ip => !PrivateAddress.IsPrivate(ip)).ToArray();
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
