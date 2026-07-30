using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using toimi.tools.selain.Browser;
using toimi.tools.selain.Tools;
using Xunit;

namespace toimi.tools.selain.Tests.Integration;

/// <summary>
/// Boots the real selain app in-memory (WebApplicationFactory) with loopback
/// fixtures allowed, then exercises the HTTP surface displays will use. Not in
/// the "selain" collection — this factory's TabManager/BrowserHost stack is
/// separate from SelainFixture's.
/// </summary>
public class EndpointTests(EndpointTests.SelainAppFactory app, FixtureSite site) : IClassFixture<EndpointTests.SelainAppFactory>, IClassFixture<FixtureSite>
{
  public sealed class SelainAppFactory : WebApplicationFactory<Program>
  {
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
      builder.UseSetting("Selain:PublicBaseUrl", "https://toimi.example");
      builder.UseSetting("Selain:AllowedPrivateHosts:0", "127.0.0.1");
      builder.UseSetting("Selain:AllowedPrivateHosts:1", "localhost");
    }
  }

  private readonly SelainAppFactory _app = app;
  private readonly FixtureSite _site = site;

  private async Task<Guid> OpenTabAsync()
  {
    var services = _app.Services;
    var tools = new BrowseTools(
      services.GetRequiredService<SelainOptions>(),
      services.GetRequiredService<UrlPolicy>(),
      services.GetRequiredService<TabManager>(),
      services.GetRequiredService<BrowserHost>());
    await tools.Browse($"{_site.BaseUrl}/mutate");
    return services.GetRequiredService<TabManager>().Active!.Id;
  }

  /// <summary>Tests share the factory's TabManager — close every tab so none couples on leftovers.</summary>
  private async Task CloseAllTabsAsync()
  {
    var tabs = _app.Services.GetRequiredService<TabManager>();
    foreach (var tab in tabs.List())
    {
      await tabs.CloseAsync(tab.Id);
    }
  }

  [ChromiumFact]
  public async Task Screenshot_endpoint_returns_png_for_a_known_tab_and_404_otherwise()
  {
    try
    {
      var id = await OpenTabAsync();
      var client = _app.CreateClient();

      var ok = await client.GetAsync($"/tabs/{id}/screenshot");
      Assert.True(ok.IsSuccessStatusCode);
      Assert.Equal("image/png", ok.Content.Headers.ContentType?.MediaType);
      // no-store: each poll's ?t= URL must not pile up in a webview's cache.
      Assert.True(ok.Headers.CacheControl?.NoStore);
      var bytes = await ok.Content.ReadAsByteArrayAsync();
      // PNG magic bytes.
      Assert.Equal(0x89, bytes[0]);
      Assert.Equal((byte)'P', bytes[1]);

      var missing = await client.GetAsync($"/tabs/{Guid.NewGuid()}/screenshot");
      Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
    }
    finally
    {
      await CloseAllTabsAsync();
    }
  }

  [ChromiumFact]
  public async Task Screenshot_endpoint_captures_a_background_tab()
  {
    try
    {
      var services = _app.Services;
      var backgroundId = await OpenTabAsync();

      // Open a second tab via the tabs tool — it becomes active, demoting the
      // first tab to background; a display must still be able to capture it.
      var tabTools = new TabTools(
        services.GetRequiredService<SelainOptions>(),
        services.GetRequiredService<UrlPolicy>(),
        services.GetRequiredService<TabManager>(),
        services.GetRequiredService<BrowserHost>());
      await tabTools.Tabs("new", url: $"{_site.BaseUrl}/mutate");
      Assert.NotEqual(backgroundId, services.GetRequiredService<TabManager>().Active!.Id);

      var response = await _app.CreateClient().GetAsync($"/tabs/{backgroundId}/screenshot");
      Assert.True(response.IsSuccessStatusCode);
      Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
      var bytes = await response.Content.ReadAsByteArrayAsync();
      // PNG magic bytes.
      Assert.Equal(0x89, bytes[0]);
      Assert.Equal((byte)'P', bytes[1]);
    }
    finally
    {
      await CloseAllTabsAsync();
    }
  }

  [ChromiumFact]
  public async Task Viewer_page_is_self_contained_html_referencing_the_tab()
  {
    try
    {
      var id = await OpenTabAsync();
      var client = _app.CreateClient();
      var response = await client.GetAsync($"/tabs/{id}/view");
      var html = await response.Content.ReadAsStringAsync();
      Assert.Contains(id.ToString(), html);
      Assert.Contains("/stream", html);
      Assert.Contains("/screenshot", html);
      // Self-contained: no external scripts, stylesheets, or fonts.
      Assert.DoesNotContain("<script src", html);
      Assert.DoesNotContain("<link", html);
    }
    finally
    {
      await CloseAllTabsAsync();
    }
  }

  [ChromiumFact]
  public async Task Screenshot_tool_returns_a_png_image_block()
  {
    try
    {
      await OpenTabAsync();
      var services = _app.Services;
      var tool = new ScreenshotTool(
        services.GetRequiredService<SelainOptions>(),
        services.GetRequiredService<TabManager>(),
        services.GetRequiredService<BrowserHost>());

      var result = await tool.Screenshot();

      var image = Assert.IsType<ImageContentBlock>(Assert.Single(result.Content));
      Assert.Equal("image/png", image.MimeType);
      // Decode the SDK's Data representation and check the PNG magic bytes.
      var bytes = image.DecodedData.ToArray();
      Assert.Equal(0x89, bytes[0]);
      Assert.Equal((byte)'P', bytes[1]);
    }
    finally
    {
      await CloseAllTabsAsync();
    }
  }

  [ChromiumFact]
  public async Task Stream_delivers_screencast_frames_for_a_mutating_page()
  {
    try
    {
      var id = await OpenTabAsync();
      var wsClient = _app.Server.CreateWebSocketClient();
      var ws = await wsClient.ConnectAsync(
        new Uri(_app.Server.BaseAddress, $"/tabs/{id}/stream"), CancellationToken.None);

      var buffer = new byte[512 * 1024];
      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
      var result = await ws.ReceiveAsync(buffer, cts.Token);

      Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Binary, result.MessageType);
      Assert.True(result.Count > 100, "expected a JPEG frame of nontrivial size");
      // JPEG magic bytes.
      Assert.Equal(0xFF, buffer[0]);
      Assert.Equal(0xD8, buffer[1]);

      // cts.Token, not None: a regressed server close-handshake must fail this
      // test, not hang the whole run.
      await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, cts.Token);
    }
    finally
    {
      await CloseAllTabsAsync();
    }
  }

  [Fact]
  public async Task Screenshot_tool_without_a_tab_returns_text_guidance()
  {
    var services = _app.Services;
    var tool = new ScreenshotTool(
      services.GetRequiredService<SelainOptions>(),
      services.GetRequiredService<TabManager>(),
      services.GetRequiredService<BrowserHost>());

    var result = await tool.Screenshot();

    var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
    Assert.Contains("browse", text.Text);
  }
}

/// <summary>Standalone fixture site for EndpointTests (SelainFixture's browser stack is separate from the app factory's).</summary>
public sealed class FixtureSite : IAsyncLifetime
{
  private WebApplication? _site;

  public string BaseUrl { get; private set; } = "";

  public async Task InitializeAsync()
  {
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    _site = builder.Build();
    _site.MapGet("/mutate", () => Microsoft.AspNetCore.Http.Results.Content(
      """<!doctype html><html><body><div id="t">start</div><script>setInterval(() => { document.getElementById('t').textContent = Date.now(); }, 300);</script></body></html>""",
      "text/html"));
    await _site.StartAsync();
    BaseUrl = _site.Urls.First();
  }

  public async Task DisposeAsync()
  {
    if (_site is not null)
    {
      await _site.StopAsync();
    }
  }
}
