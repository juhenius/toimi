using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using toimi.tools.selain.Browser;
using Xunit;

namespace toimi.tools.selain.Tests.Integration;

/// <summary>
/// Collection fixture: a loopback Kestrel site serving fixture pages, plus the
/// real browser stack (UrlPolicy allowing loopback, TabManager, BrowserHost).
/// Chromium-gated tests share one browser across the collection.
/// </summary>
public sealed class SelainFixture : IAsyncLifetime
{
  private WebApplication? _site;

  public string BaseUrl { get; private set; } = "";

  /// <summary>
  /// Same fixture server, reachable on a loopback host that is NOT allowlisted
  /// (127.0.0.2 is loopback → PrivateAddress blocks it, and only 127.0.0.1 and
  /// localhost are allowlisted). A redirect here actually commits, so it forces
  /// the navigation-committed SSRF guard to fire — unlike an unroutable 10.x host.
  /// </summary>
  public string DisallowedLoopbackUrl { get; private set; } = "";

  public SelainOptions Options { get; } = new()
  {
    PublicBaseUrl = "https://toimi.example",
    AllowedPrivateHosts = ["127.0.0.1", "localhost"]
  };
  public UrlPolicy Policy { get; private set; } = null!;
  public TabManager Tabs { get; private set; } = null!;
  public BrowserHost Host { get; private set; } = null!;

  public async Task InitializeAsync()
  {
    var port = PickFreePort();
    var builder = WebApplication.CreateBuilder();
    // Bind the same port on both loopback IPs: 127.0.0.1 is the allowlisted
    // origin, 127.0.0.2 is the disallowed-but-responding redirect target.
    builder.WebHost.UseUrls($"http://127.0.0.1:{port}", $"http://127.0.0.2:{port}");
    builder.Logging.ClearProviders();
    _site = builder.Build();

    _site.MapGet("/static", () => Page("<h1>Static page</h1><p>plain content here</p>"));
    _site.MapGet("/js", () => Page(
      "<h1>Shell</h1><script>setTimeout(() => { document.body.insertAdjacentHTML('beforeend', '<p id=\"late\">Hydrated content arrived</p>'); }, 200);</script>"));
    _site.MapGet("/form", () => Page(
      "<label for=\"name\">Your name</label><input id=\"name\" type=\"text\"> "
      + "<select id=\"pick\" aria-label=\"Pick one\"><option value=\"a\">Alpha</option><option value=\"b\">Beta</option></select> "
      + "<button onclick=\"document.getElementById('out').textContent = document.getElementById('name').value + '/' + document.getElementById('pick').value\">Send</button> "
      + "<p id=\"out\"></p>"));
    _site.MapGet("/popup", () => Page("<a href=\"/static\" target=\"_blank\">open popup</a>"));
    _site.MapGet("/dialog", () => Page("<button onclick=\"alert('hello from dialog')\">Alert me</button>"));
    _site.MapGet("/hover", () => Page(
      "<style>#menu span { display: none } #menu:hover span { display: inline }</style>"
      + "<div id=\"menu\">Menu<span> revealed-by-hover</span></div>"));
    _site.MapGet("/subres", () => Page(
      "<h1>Subresource probe</h1><img src=\"http://10.255.255.1/x.png\" "
      + "onerror=\"document.body.insertAdjacentHTML('beforeend', '<p>subresource-blocked</p>')\">"));
    _site.MapGet("/mutate", () => Page(
      "<div id=\"t\">start</div><script>setInterval(() => { document.getElementById('t').textContent = Date.now(); }, 300);</script>"));
    // Server-side redirect to an unroutable private host (spec-named probe).
    _site.MapGet("/redir-private", () => Results.Redirect("http://10.255.255.1/x"));
    // Server-side redirect to the same server on a disallowed loopback host —
    // this one responds, so it commits and truly exercises the redirect guard.
    _site.MapGet("/redir-loopback2", () => Results.Redirect($"http://127.0.0.2:{port}/static"));
    // Hostile page embedding an iframe whose src (allowed first hop) 302s to a
    // disallowed host — probes the SUBFRAME committed-redirect SSRF gap.
    _site.MapGet("/iframe-redir", () => Page("<h1>Outer</h1><iframe src=\"/redir-loopback2\"></iframe>"));

    await _site.StartAsync();
    BaseUrl = $"http://127.0.0.1:{port}";
    DisallowedLoopbackUrl = $"http://127.0.0.2:{port}";

    Policy = new UrlPolicy(Options);
    Tabs = new TabManager(Options);
    Host = new BrowserHost(Options, Policy, Tabs, NullLogger<BrowserHost>.Instance);
  }

  public async Task DisposeAsync()
  {
    await Host.DisposeAsync();
    if (_site is not null)
    {
      await _site.StopAsync();
    }
  }

  private static IResult Page(string body)
  {
    return Results.Content($"<!doctype html><html><body>{body}</body></html>", "text/html");
  }

  private static int PickFreePort()
  {
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return port;
  }
}

[CollectionDefinition("selain")]
public class SelainCollectionDefinition : ICollectionFixture<SelainFixture>;
