using Microsoft.Playwright;
using toimi.tools.selain.Browser;
using toimi.tools.selain.Tools;
using Xunit;

namespace toimi.tools.selain.Tests.Integration;

[Collection("selain")]
public class BrowserToolTests(SelainFixture fx)
{
  [ChromiumFact]
  public async Task AriaSnapshot_produces_refs_that_resolve_to_elements()
  {
    var context = await fx.Host.GetContextAsync();
    var page = await context.NewPageAsync();
    try
    {
      await page.GotoAsync($"{fx.BaseUrl}/form");
      // Playwright .NET 1.61 exposes refs via Mode = Ai (no bool Ref option).
      var snapshot = await page.AriaSnapshotAsync(new() { Mode = AriaSnapshotMode.Ai });

      Assert.Contains("[ref=", snapshot);

      var start = snapshot.IndexOf("[ref=", StringComparison.Ordinal) + "[ref=".Length;
      var elementRef = snapshot[start..snapshot.IndexOf(']', start)];
      Assert.Equal(1, await page.Locator($"aria-ref={elementRef}").CountAsync());
    }
    finally
    {
      await page.CloseAsync();
    }
  }

  [ChromiumFact]
  public async Task Server_side_redirect_to_disallowed_host_is_bounced_to_about_blank()
  {
    var context = await fx.Host.GetContextAsync();
    var page = await context.NewPageAsync();
    Guid? tabId = null;
    try
    {
      // /redir-loopback2 302s to 127.0.0.2 (responds, but not allowlisted). The
      // route guard never sees the redirect target, so only the navigation-
      // committed guard can stop it. Navigation may resolve or throw as the
      // guard yanks the page to about:blank mid-flight — either is fine.
      try
      {
        await page.GotoAsync($"{fx.BaseUrl}/redir-loopback2", new() { Timeout = 10_000 });
      }
      catch (PlaywrightException)
      {
        // Guard bounced the in-flight navigation; synchronised on below.
      }

      // The guard bounces the page to about:blank; it may land there before or
      // after GotoAsync unwinds, so poll rather than wait for a fresh navigation
      // (WaitForURLAsync would hang if the URL already matches at call time).
      var deadline = DateTime.UtcNow.AddSeconds(10);
      while (page.Url != "about:blank" && DateTime.UtcNow < deadline)
      {
        await Task.Delay(50);
      }

      // The invariant: the browser must NOT end up loaded on the disallowed host.
      Assert.Equal("about:blank", page.Url);

      tabId = fx.Tabs.FindByHandle(page);
      Assert.NotNull(tabId);
      Assert.Equal("[blocked redirect to 127.0.0.2]", fx.Tabs.TakeDialogNote(tabId.Value));
    }
    finally
    {
      await page.CloseAsync();
      if (tabId is { } id)
      {
        await fx.Tabs.CloseAsync(id);
      }
    }
  }

  [ChromiumFact]
  public async Task Subframe_redirect_to_disallowed_host_bounces_the_whole_page()
  {
    var context = await fx.Host.GetContextAsync();
    var page = await context.NewPageAsync();
    Guid? tabId = null;
    try
    {
      // The outer page is on the allowlisted host; the iframe's first hop
      // (/redir-loopback2, same allowed host) passes the route guard, and its
      // 302 target (127.0.0.2, disallowed but responding) never re-enters
      // routing in Playwright 1.61 — only the navigation-committed guard can
      // catch it. A page embedding such an iframe is hostile by definition, so
      // the WHOLE page must bounce to about:blank.
      try
      {
        await page.GotoAsync($"{fx.BaseUrl}/iframe-redir", new() { Timeout = 10_000 });
      }
      catch (PlaywrightException)
      {
        // Guard bounced the in-flight navigation; synchronised on below.
      }

      var deadline = DateTime.UtcNow.AddSeconds(10);
      while (page.Url != "about:blank" && DateTime.UtcNow < deadline)
      {
        await Task.Delay(50);
      }

      // On failure, show where every frame ended up — proves whether the
      // subframe actually committed the disallowed host.
      var frames = string.Join(", ", page.Frames.Select(f => $"'{f.Url}'"));
      Assert.True(page.Url == "about:blank", $"page was not bounced; url={page.Url}; frames: {frames}");

      tabId = fx.Tabs.FindByHandle(page);
      Assert.NotNull(tabId);
      Assert.Equal("[blocked redirect to 127.0.0.2]", fx.Tabs.TakeDialogNote(tabId.Value));
    }
    finally
    {
      await page.CloseAsync();
      if (tabId is { } id)
      {
        await fx.Tabs.CloseAsync(id);
      }
    }
  }

  [ChromiumFact]
  public async Task Context_page_event_adopts_new_pages_into_tab_manager()
  {
    var context = await fx.Host.GetContextAsync();
    var page = await context.NewPageAsync();
    try
    {
      Assert.NotNull(fx.Tabs.FindByHandle(page));
    }
    finally
    {
      var id = fx.Tabs.FindByHandle(page);
      await page.CloseAsync();
      if (id is { } tabId)
      {
        await fx.Tabs.CloseAsync(tabId);
      }
    }
  }

  private BrowseTools NewBrowseTools()
  {
    return new BrowseTools(fx.Options, fx.Policy, fx.Tabs, fx.Host);
  }

  /// <summary>
  /// Isolation cleanup: browse tools reuse the shared TabManager's active tab,
  /// and identical page content hashes to "(page unchanged)" across tests —
  /// closing the tab resets LastShownHash so tests don't couple on run order.
  /// </summary>
  private async Task CloseActiveTabAsync()
  {
    if (fx.Tabs.Active is { } tab)
    {
      await fx.Tabs.CloseAsync(tab.Id);
    }
  }

  [ChromiumFact]
  public async Task Browse_returns_title_url_and_ref_snapshot()
  {
    try
    {
      var result = await NewBrowseTools().Browse($"{fx.BaseUrl}/static?t=browse-basic");
      Assert.Contains("Static page", result);
      Assert.Contains("URL:", result);
      Assert.Contains("[ref=", result);
    }
    finally
    {
      await CloseActiveTabAsync();
    }
  }

  [ChromiumFact]
  public async Task Browse_rejects_private_hosts_with_a_friendly_error()
  {
    var result = await NewBrowseTools().Browse("http://10.1.2.3/");
    Assert.Contains("private or internal", result);
  }

  [ChromiumFact]
  public async Task Snapshot_repeat_without_changes_reports_page_unchanged()
  {
    try
    {
      var tools = NewBrowseTools();
      await tools.Browse($"{fx.BaseUrl}/static?t=snapshot-unchanged");
      var second = await tools.Snapshot();
      Assert.Contains("(page unchanged)", second);
    }
    finally
    {
      await CloseActiveTabAsync();
    }
  }

  [ChromiumFact]
  public async Task Disabled_kill_switch_short_circuits_tools()
  {
    var offOptions = new SelainOptions { Enabled = false };
    var tools = new BrowseTools(offOptions, fx.Policy, fx.Tabs, fx.Host);
    Assert.Contains("disabled", await tools.Browse($"{fx.BaseUrl}/static"));
  }
}
