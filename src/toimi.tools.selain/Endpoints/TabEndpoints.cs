using Microsoft.Playwright;
using toimi.tools.selain.Browser;
using toimi.tools.selain.Streaming;

namespace toimi.tools.selain.Endpoints;

/// <summary>
/// Display-facing HTTP surface. The unguessable tab GUID is the capability:
/// endpoints only answer for tabs the agent opened, and ids die with the tab.
/// Deliberately lock-free — a display polling screenshots is a read-only
/// capture and must not queue behind agent actions on the ActionLock.
/// </summary>
public static class TabEndpoints
{
  public static void MapTabEndpoints(this WebApplication app)
  {
    app.MapGet("/tabs/{id:guid}/screenshot", async (Guid id, TabManager tabs, HttpContext context) =>
    {
      // Each poll uses a fresh ?t= URL, which is heuristically cacheable — a
      // long-running display webview would accumulate stale PNGs without this.
      context.Response.Headers.CacheControl = "no-store";

      if (tabs.Get(id) is not { } tab)
      {
        return Results.NotFound();
      }

      var page = ((PlaywrightSession)tab.Session).Page;
      try
      {
        var bytes = await page.ScreenshotAsync(new() { Type = ScreenshotType.Png });
        return Results.File(bytes, "image/png");
      }
      catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
      {
        // The page closed between the lookup and the capture — to the polling
        // viewer that's the same as an unknown tab, not a server error.
        return Results.NotFound();
      }
    });

    app.MapGet("/tabs/{id:guid}/view", (Guid id, TabManager tabs) =>
      tabs.Get(id) is null ? Results.NotFound() : Results.Content(ViewerPage.Html(id), "text/html"));

    app.Map("/tabs/{id:guid}/stream", async (HttpContext context, Guid id, TabManager tabs, ScreencastService screencast) =>
    {
      if (!context.WebSockets.IsWebSocketRequest)
      {
        return Results.BadRequest("WebSocket endpoint.");
      }
      if (tabs.Get(id) is not { } tab)
      {
        return Results.NotFound();
      }
      using var socket = await context.WebSockets.AcceptWebSocketAsync();
      await screencast.StreamAsync(((PlaywrightSession)tab.Session).Page, socket, context.RequestAborted);
      return Results.Empty;
    });
  }
}
