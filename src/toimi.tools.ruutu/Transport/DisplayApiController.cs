using Microsoft.AspNetCore.Mvc;
using toimi.tools.ruutu.Data.Repositories;

namespace toimi.tools.ruutu.Transport;

[ApiController]
[Route("ruutu")]
public class DisplayApiController(
  DisplayRepository displays,
  IWebHostEnvironment env,
  ILogger<DisplayApiController> logger) : ControllerBase
{
  [HttpGet("{identifier}")]
  public async Task<IActionResult> GetShell(string identifier, CancellationToken ct)
  {
    var display = await displays.GetAsync(identifier, ct);
    if (display is null)
      return Content(NotConfiguredPage(identifier), "text/html");

    var shellPath = Path.Combine(env.WebRootPath, "shell.html");
    var html = await System.IO.File.ReadAllTextAsync(shellPath, ct);
    html = html.Replace("__IDENTIFIER__", identifier);
    return Content(html, "text/html");
  }

  public record CapabilitiesRequest(
    bool Flexbox, bool CssGrid, bool Fetch, bool Promise,
    int ViewportWidth, int ViewportHeight, string UserAgent);

  [HttpPost("api/displays/{identifier}/capabilities")]
  public async Task<IActionResult> PostCapabilities(
    string identifier, [FromBody] CapabilitiesRequest req, CancellationToken ct)
  {
    var display = await displays.GetAsync(identifier, ct);
    if (display is null) return NotFound();

    var payload = new toimi.tools.ruutu.Rendering.CapabilityPayload(
      req.Flexbox, req.CssGrid, req.Fetch, req.Promise,
      req.ViewportWidth, req.ViewportHeight, req.UserAgent ?? "");

    string tier;
    string orientation;
    try
    {
      tier = toimi.tools.ruutu.Rendering.CapabilityClassifier.Classify(payload);
      orientation = toimi.tools.ruutu.Rendering.CapabilityClassifier.DeriveOrientation(
        payload.ViewportWidth, payload.ViewportHeight);
    }
#pragma warning disable CA1031 // Defensive fallback to legacy on any classifier failure; we log so bugs in the pure-function classifier still surface.
    catch (Exception ex)
#pragma warning restore CA1031
    {
      logger.LogWarning(ex, "Capability classification failed for '{Identifier}'; falling back to legacy/landscape", identifier);
      tier = "legacy";
      orientation = "landscape";
    }

    await displays.RecordCapabilitiesAsync(
      identifier, tier, payload.UserAgent,
      payload.ViewportWidth, payload.ViewportHeight, orientation, ct);
    return Ok();
  }

  [HttpGet("api/displays/{identifier}/stream")]
  public async Task StreamAsync(
    string identifier,
    [FromServices] SseHub hub,
    [FromServices] DisplayRepository displaysRepo,
    [FromServices] ContentPushService pusher,
    [FromServices] IServiceScopeFactory scopeFactory,
    CancellationToken ct)
  {
    var display = await displaysRepo.GetAsync(identifier, ct);
    if (display is null) { Response.StatusCode = 404; return; }

    Response.ContentType = "text/event-stream";
    Response.Headers.CacheControl = "no-cache";
    Response.Headers["X-Accel-Buffering"] = "no";

    var channel = hub.Subscribe(identifier);

    try
    {
      await pusher.ReplayCurrentStateAsync(identifier, ct);
    }
    catch (Exception ex)
    {
      await WriteSseAsync("error", $"{{\"message\":\"{ex.Message}\"}}", ct);
    }

    using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var heartbeatTask = Task.Run(async () =>
    {
      while (!heartbeatCts.IsCancellationRequested)
      {
        try
        {
          await Task.Delay(TimeSpan.FromSeconds(15), heartbeatCts.Token);
          await hub.PublishAsync(identifier, new SseEvent("heartbeat", "{}"), heartbeatCts.Token);
          // Use a fresh DI scope per heartbeat so we don't keep the request-scoped
          // DbContext alive for the lifetime of the SSE connection (potentially days).
          using var scope = scopeFactory.CreateScope();
          var repo = scope.ServiceProvider.GetRequiredService<DisplayRepository>();
          await repo.UpdateLastSeenAsync(identifier, heartbeatCts.Token);
        }
        catch (OperationCanceledException) { break; }
      }
    }, heartbeatCts.Token);

    try
    {
      await foreach (var ev in channel.Reader.ReadAllAsync(ct))
      {
        await WriteSseAsync(ev.EventType, ev.JsonPayload, ct);
      }
    }
    catch (OperationCanceledException) { }
    finally
    {
      await heartbeatCts.CancelAsync();
      hub.Unsubscribe(identifier, channel);
      await heartbeatTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
  }

  public record EventRequest(string Type, string? Target, object? Value);

  [HttpPost("api/displays/{identifier}/events")]
  public async Task<IActionResult> PostEvent(
    string identifier,
    [FromBody] EventRequest req,
    [FromServices] DisplayEventRepository events,
    [FromServices] ContentPushService pusher,
    CancellationToken ct)
  {
    var display = await displays.GetAsync(identifier, ct);
    if (display is null) return NotFound();

    await displays.UpdateLastSeenAsync(identifier, ct);

    var valueJson = req.Value is null ? null
      : System.Text.Json.JsonSerializer.Serialize(req.Value);

    await events.AppendAsync(display.Id, req.Type, req.Target, valueJson, ct);

    if (req.Type == "dismiss" && req.Target == "overlay")
    {
      await pusher.DismissTopOverlayAsync(identifier, ct);
    }

    return Ok();
  }

  private async Task WriteSseAsync(string type, string json, CancellationToken ct)
  {
    await Response.WriteAsync($"event: {type}\n", ct);
    await Response.WriteAsync($"data: {json}\n\n", ct);
    await Response.Body.FlushAsync(ct);
  }

  private static string NotConfiguredPage(string identifier) =>
    $$"""
    <!DOCTYPE html><html><head><meta charset="utf-8"><title>not configured</title>
    <style>body{font-family:-apple-system,system-ui,sans-serif;background:#f5f3ef;padding:40px;text-align:center;color:#444}</style>
    </head><body>
      <h1>Display '{{identifier}}' is not configured.</h1>
      <p>Ask Toimi to register this display, then refresh this page.</p>
    </body></html>
    """;
}
