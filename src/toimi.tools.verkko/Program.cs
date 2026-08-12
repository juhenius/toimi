using toimi.tools.verkko.Fetcher;
using Toimi.Core.Hosting;
using Toimi.Notifications;

var builder = WebApplication.CreateBuilder(args);

// Web fetching
builder.Services.AddHttpClient<WebFetcher>(client =>
{
  client.Timeout = TimeSpan.FromSeconds(15);
  client.DefaultRequestHeaders.UserAgent.ParseAdd("Toimi/1.0 (personal assistant)");
  // Refuse oversized bodies before buffering them into memory (OOM guard).
  // Slightly above the 50k the fetcher keeps, so truncation still applies to normal pages.
  client.MaxResponseContentBufferSize = 8_000_000;
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
  // SSRF guard: validates every connection (incl. redirect targets) against private ranges.
  ConnectCallback = UrlGuard.GuardedConnectAsync,
  // A proxy would bypass the guard (only the proxy address gets validated).
  UseProxy = false,
  // Bounds DNS + connect inside the callback; the default is infinite.
  ConnectTimeout = TimeSpan.FromSeconds(10)
});
builder.Services.AddSingleton<FetchCache>();

// Notifications (ntfy)
var ntfyOptions = builder.Configuration.GetSection("Ntfy").Get<NtfyOptions>() ?? new NtfyOptions();
builder.Services.AddSingleton(ntfyOptions);
builder.Services.AddSingleton(new NtfyClient(ntfyOptions));

builder.AddToimiToolServer("verkko", typeof(Program).Assembly);

var app = builder.Build();

app.MapToimiMcp();

app.Run();
