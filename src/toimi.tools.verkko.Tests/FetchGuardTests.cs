using toimi.tools.verkko.Fetcher;
using toimi.tools.verkko.Tools;
using Xunit;

namespace toimi.tools.verkko.Tests;

public class FetchGuardTests
{
  private static HttpClient GuardedClient()
  {
    return new HttpClient(new SocketsHttpHandler { ConnectCallback = UrlGuard.GuardedConnectAsync });
  }

  [Fact]
  public async Task FetchUrl_rejects_private_ip_literal_before_fetching()
  {
    var client = GuardedClient();
    var tool = new FetchUrlTool(new WebFetcher(client), new FetchCache());

    var result = await tool.FetchUrl("http://192.168.1.10/admin");

    Assert.Contains("private or internal", result);
  }

  [Fact]
  public async Task FetchUrl_rejects_single_label_host()
  {
    var client = GuardedClient();
    var tool = new FetchUrlTool(new WebFetcher(client), new FetchCache());

    var result = await tool.FetchUrl("http://qdrant:6333/collections");

    Assert.Contains("private or internal", result);
  }

  [Fact]
  public async Task GuardedHandler_blocks_connections_to_private_literals()
  {
    // The callback rejects before any socket is opened, so no network is needed.
    using var client = GuardedClient();

    var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://10.11.12.13:8080/"));

    var messages = ex.Message + " " + (ex.InnerException?.Message ?? "");
    Assert.Contains("private or internal", messages);
  }

  [Fact]
  public async Task GuardedHandler_blocks_hostnames_resolving_to_private_addresses()
  {
    // "localhost" resolves via the hosts file to loopback — exercises the DNS branch offline.
    using var client = GuardedClient();

    var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://localhost:9/"));

    var messages = ex.Message + " " + (ex.InnerException?.Message ?? "");
    Assert.Contains("private or internal", messages);
  }

  [Fact]
  public async Task GuardedHandler_blocks_ipv4_mapped_ipv6_literals()
  {
    using var client = GuardedClient();

    var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://[::ffff:192.168.1.10]/"));

    var messages = ex.Message + " " + (ex.InnerException?.Message ?? "");
    Assert.Contains("private or internal", messages);
  }
}
