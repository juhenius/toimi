using Microsoft.Playwright;
using toimi.tools.selain.Browser;

namespace toimi.tools.selain.Tools;

internal static class ToolGuard
{
  public const string TabLostMessage = "The tab is no longer available (closed or browser restarted) — use browse to start again.";
  public const string PageBusyMessage = "The page is busy and did not respond in time — try again, or use wait_for.";

  /// <summary>Non-null message when the global kill switch is off.</summary>
  public static string? Disabled(SelainOptions options)
  {
    return options.Enabled ? null : "Browser tools are disabled (Selain:Enabled=false).";
  }

  /// <summary>
  /// Shared guard for tools that operate on the active tab: kill switch,
  /// idle-clock touch, ActionLock, no-tab check, and the friendly-error
  /// contract — page-level failures (tab crashed/closed mid-call, Playwright
  /// timeouts) come back as tool text, never as a raw exception out of the MCP
  /// tool. A bare timeout is a busy page, not a lost tab, so it gets its own
  /// message. SemaphoreSlim is not reentrant, so never nest this inside a
  /// lock-holding path.
  /// </summary>
  public static async Task<string> WithActiveTabAsync(SelainOptions options, TabManager tabs, BrowserHost host, Func<TabManager.TabEntry, Task<string>> body)
  {
    if (Disabled(options) is { } off)
    {
      return off;
    }

    host.Touch();
    await tabs.ActionLock.WaitAsync();
    try
    {
      if (tabs.Active is not { } active)
      {
        return "No open tab — use browse first.";
      }

      try
      {
        return await body(active);
      }
      catch (TimeoutException)
      {
        return PageBusyMessage;
      }
      catch (PlaywrightException)
      {
        return TabLostMessage;
      }
    }
    finally
    {
      tabs.ActionLock.Release();
    }
  }
}
