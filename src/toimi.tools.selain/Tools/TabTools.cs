using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Playwright;
using ModelContextProtocol.Server;
using toimi.tools.selain.Browser;

namespace toimi.tools.selain.Tools;

[McpServerToolType]
public class TabTools(SelainOptions options, UrlPolicy policy, TabManager tabs, BrowserHost host)
{
  [McpServerTool, Description("Manage browser tabs. action=list|new|switch|close. 'new' accepts an optional url and viewport width/height (default 1280x720 — set to a display's size when the tab will be streamed to it). 'list' shows each tab's id, URL, and its viewer URL for ruutu's webview template. Actions apply to tabId (a GUID from 'list').")]
  public async Task<string> Tabs(
    [Description("list | new | switch | close")] string action,
    [Description("Tab id (GUID from 'list') — required for switch/close")] string? tabId = null,
    [Description("URL to open in the new tab (action=new)")] string? url = null,
    [Description("Viewport width for the new tab")] int? width = null,
    [Description("Viewport height for the new tab")] int? height = null)
  {
    if (ToolGuard.Disabled(options) is { } off)
    {
      return off;
    }

    host.Touch();
    await tabs.ActionLock.WaitAsync();
    try
    {
      switch (action)
      {
        case "list":
          return ListTabs();
        case "new":
          return await NewTabAsync(url, width, height);
        case "switch":
        case "close":
          {
            return !Guid.TryParse(tabId, out var id)
              ? $"Invalid tab id '{tabId}' — use a GUID from tabs(list)."
              : action == "switch"
              ? tabs.Switch(id) ? $"Switched to tab {id}." : $"Tab {id} not found."
              : await tabs.CloseAsync(id) ? $"Tab {id} closed." : $"Tab {id} not found.";
          }
        default:
          return $"Unknown action '{action}' — use list, new, switch, or close.";
      }
    }
    finally
    {
      tabs.ActionLock.Release();
    }
  }

  private string ListTabs()
  {
    var entries = tabs.List();
    if (entries.Count == 0)
    {
      return "No open tabs.";
    }

    var sb = new StringBuilder();
    foreach (var entry in entries)
    {
      // The [active] marker must directly follow the id — callers parse the
      // id as the first token after the "- " prefix.
      var marker = entry.Id == tabs.Active?.Id ? " [active]" : "";
      sb.AppendLine(CultureInfo.InvariantCulture, $"- {entry.Id}{marker} {entry.Session.Url}");
      sb.AppendLine(CultureInfo.InvariantCulture, $"  viewer: {tabs.ViewerUrl(entry.Id)}");
    }

    return sb.ToString().TrimEnd();
  }

  private async Task<string> NewTabAsync(string? url, int? width, int? height)
  {
    // Validate before opening anything so a rejected request doesn't leak a blank tab.
    if (width.HasValue != height.HasValue)
    {
      return "width and height must be given together.";
    }

    if (width is < 200 or > 4000 || height is < 200 or > 4000)
    {
      return $"Viewport {width}x{height} is out of range — use 200-4000 per side.";
    }

    Uri? target = null;
    if (url is not null)
    {
      var (ok, error, uri) = policy.Validate(url);
      if (!ok)
      {
        return error!;
      }

      target = uri;
    }

    IPage page;
    try
    {
      var context = await host.GetContextAsync();
      page = await context.NewPageAsync();
    }
    catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
    {
      // Chromium failed to launch or died while opening the page — a tool
      // answer, never a raw MCP error.
      return $"Browser failed to start: {ex.Message}";
    }

    // The context's Page event usually adopts first; Adopt is idempotent either way.
    var id = tabs.FindByHandle(page) ?? tabs.Adopt(new PlaywrightSession(page));
    tabs.Switch(id);

    var size = "";
    if (width is { } w && height is { } h)
    {
      try
      {
        await page.SetViewportSizeAsync(w, h);
      }
      catch (PlaywrightException)
      {
        return ToolGuard.TabLostMessage;
      }

      size = $" (viewport {w}x{h})";
    }

    if (target is null)
    {
      return $"Opened tab {id}{size}. Viewer: {tabs.ViewerUrl(id)}";
    }

    try
    {
      await page.GotoAsync(target.ToString(), new() { WaitUntil = WaitUntilState.Load, Timeout = 20_000 });
    }
    catch (TimeoutException)
    {
      return $"Opened tab {id}{size}, but navigation to {url} timed out after 20s. Viewer: {tabs.ViewerUrl(id)}";
    }
    catch (PlaywrightException ex)
    {
      return $"Opened tab {id}{size}, but navigation failed: {ex.Message}\nViewer: {tabs.ViewerUrl(id)}";
    }

    if (tabs.Get(id) is not { } active)
    {
      // The page closed itself during navigation and was reaped.
      return ToolGuard.TabLostMessage;
    }

    var result = await PageResults.ComposeGuardedAsync(tabs, host, active);
    return $"Opened tab {id}{size}. Viewer: {tabs.ViewerUrl(id)}\n{result}";
  }
}
