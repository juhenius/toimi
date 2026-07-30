using System.ComponentModel;
using Microsoft.Playwright;
using ModelContextProtocol.Server;
using toimi.tools.selain.Browser;

namespace toimi.tools.selain.Tools;

[McpServerToolType]
public class BrowseTools(SelainOptions options, UrlPolicy policy, TabManager tabs, BrowserHost host)
{
  [McpServerTool, Description("Open a URL in the browser's active tab (opening a first tab if none) and return an accessibility snapshot with element refs like [ref=e5], usable with click/type/hover/select_option. Prefer verkko's fetch_url for simple static pages — use browse when a page needs JavaScript, interaction, or a display feed.")]
  public async Task<string> Browse([Description("Absolute http(s) URL")] string url)
  {
    if (ToolGuard.Disabled(options) is { } off)
    {
      return off;
    }

    var (ok, error, uri) = policy.Validate(url);
    if (!ok)
    {
      return error!;
    }

    await tabs.ActionLock.WaitAsync();
    try
    {
      TabManager.TabEntry? active;
      try
      {
        var context = await host.GetContextAsync();
        active = tabs.Active;
        if (active is null)
        {
          var page = await context.NewPageAsync();
          var id = tabs.FindByHandle(page) ?? tabs.Adopt(new PlaywrightSession(page));
          tabs.Switch(id);
          active = tabs.Get(id);
        }
      }
      catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
      {
        // Chromium failed to launch or died while opening the page — a tool
        // answer, never a raw MCP error.
        return $"Browser failed to start: {ex.Message}";
      }

      if (active is null)
      {
        // The page closed in the instant between adoption and lookup.
        return ToolGuard.TabLostMessage;
      }

      var target = ((PlaywrightSession)active.Session).Page;
      try
      {
        await target.GotoAsync(uri!.ToString(), new() { WaitUntil = WaitUntilState.Load, Timeout = 20_000 });
        try
        {
          // Settle: brief network-quiet so late-hydrating SPAs get a chance;
          // pages that poll forever fall through after 3s instead of hanging.
          await target.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 3_000 });
        }
        catch (TimeoutException)
        {
          // Expected on busy pages — snapshot whatever is there.
        }
      }
      catch (TimeoutException)
      {
        return $"Navigation to {url} timed out after 20s.";
      }
      catch (PlaywrightException ex)
      {
        return $"Navigation to {url} failed: {ex.Message}";
      }

      return await PageResults.ComposeGuardedAsync(tabs, host, active);
    }
    finally
    {
      tabs.ActionLock.Release();
    }
  }

  [McpServerTool, Description("Re-read the active tab: fresh accessibility snapshot with refs. Use after waiting for dynamic content. Returns '(page unchanged)' if nothing differs from what you last saw.")]
  public Task<string> Snapshot()
  {
    return ToolGuard.WithActiveTabAsync(options, tabs, host, active => PageResults.ComposeAsync(tabs, host, active));
  }

  [McpServerTool, Description("Plain extracted text of the active tab's page (up to 50K chars) — for reading long articles where the accessibility snapshot is noise.")]
  public Task<string> ReadPage()
  {
    return ToolGuard.WithActiveTabAsync(options, tabs, host, async active =>
    {
      var page = ((PlaywrightSession)active.Session).Page;
      var text = await page.InnerTextAsync("body", new() { Timeout = 10_000 });
      return $"URL: {page.Url}\n\n{SnapshotFormatter.Truncate(text, SnapshotFormatter.ReadCap)}";
    });
  }
}
