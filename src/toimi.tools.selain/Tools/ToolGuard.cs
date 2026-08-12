using Microsoft.Playwright;
using toimi.tools.selain.Browser;
using CoreToolGuard = Toimi.Core.Tools.ToolGuard;

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
  /// The friendly-error translations for page-level failures: a bare timeout
  /// is a busy page, not a lost tab, so it gets its own message. Anything
  /// else falls through to the core guard's backstop.
  /// </summary>
  public static string? TranslatePageFailure(Exception ex)
  {
    return ex switch
    {
      TimeoutException => PageBusyMessage,
      PlaywrightException => TabLostMessage,
      _ => null,
    };
  }

  /// <summary>
  /// Shared guard for tools that operate on the active tab: kill switch,
  /// idle-clock touch, ActionLock, no-tab check, and the friendly-error
  /// contract via the core ToolGuard — page-level failures (tab crashed or
  /// closed mid-call, Playwright timeouts) come back as tool text, never as
  /// a raw exception out of the MCP tool. SemaphoreSlim is not reentrant, so
  /// never nest this inside a lock-holding path.
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
      return tabs.Active is not { } active
        ? "No open tab — use browse first."
        : await CoreToolGuard.RunAsync(() => body(active), translate: TranslatePageFailure);
    }
    finally
    {
      tabs.ActionLock.Release();
    }
  }
}
