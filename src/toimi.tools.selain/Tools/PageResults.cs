using Microsoft.Playwright;
using toimi.tools.selain.Browser;

namespace toimi.tools.selain.Tools;

/// <summary>
/// Composes the standard tool result: restart/dialog notices + title/URL + the
/// capped aria snapshot — or "(page unchanged)" when the snapshot hash matches
/// what the agent last saw for this tab.
/// </summary>
internal static class PageResults
{
  /// <summary>ComposeAsync under the friendly-error contract (for callers already holding the ActionLock).</summary>
  public static async Task<string> ComposeGuardedAsync(TabManager tabs, BrowserHost host, TabManager.TabEntry tab)
  {
    try
    {
      return await ComposeAsync(tabs, host, tab);
    }
    catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
    {
      return ToolGuard.TabLostMessage;
    }
  }

  public static async Task<string> ComposeAsync(TabManager tabs, BrowserHost host, TabManager.TabEntry tab)
  {
    var page = ((PlaywrightSession)tab.Session).Page;
    var snapshot = await page.AriaSnapshotAsync(new() { Mode = AriaSnapshotMode.Ai });
    var hash = SnapshotFormatter.Hash(snapshot);

    string body;
    if (tab.LastShownHash == hash)
    {
      body = "(page unchanged)";
    }
    else
    {
      tab.LastShownHash = hash;
      body = SnapshotFormatter.Truncate(snapshot, SnapshotFormatter.ActionCap);
    }

    var notice = host.ConsumeRestartNotice() ? "Note: browser restarted — all previous tabs were lost.\n" : "";
    var dialog = tabs.TakeDialogNote(tab.Id) is { } note ? note + "\n" : "";
    var title = await page.TitleAsync();
    return $"{notice}{dialog}Title: {title}\nURL: {page.Url}\n\n{body}";
  }
}
