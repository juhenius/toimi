using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using toimi.tools.selain.Browser;
using toimi.tools.selain.Tools;
using Xunit;

namespace toimi.tools.selain.Tests;

public class TabGuardTests
{
  private static (SelainOptions Options, TabManager Tabs, BrowserHost Host) Stack()
  {
    var options = new SelainOptions();
    var tabs = new TabManager(options);
    var host = new BrowserHost(options, new UrlPolicy(options), tabs, NullLogger<BrowserHost>.Instance);
    return (options, tabs, host);
  }

  [Fact]
  public async Task Timeout_reports_busy_page_not_lost_tab()
  {
    var (options, tabs, host) = Stack();
    tabs.Adopt(new FakePageSession());

    var result = await TabGuard.WithActiveTabAsync(options, tabs, host, _ => throw new TimeoutException());

    Assert.Contains("busy", result);
    Assert.Contains("wait_for", result);
    Assert.NotEqual(TabGuard.TabLostMessage, result);
  }

  [Fact]
  public async Task Playwright_failure_reports_lost_tab()
  {
    var (options, tabs, host) = Stack();
    tabs.Adopt(new FakePageSession());

    var result = await TabGuard.WithActiveTabAsync(options, tabs, host, _ => throw new PlaywrightException("boom"));

    Assert.Equal(TabGuard.TabLostMessage, result);
  }

  [Fact]
  public async Task Tool_action_touches_last_use_for_idle_accounting()
  {
    var (options, tabs, host) = Stack();
    tabs.Adopt(new FakePageSession());
    var before = host.LastUse;
    await Task.Delay(20);

    await TabGuard.WithActiveTabAsync(options, tabs, host, _ => Task.FromResult("ok"));

    Assert.True(host.LastUse > before, "tool action must refresh LastUse so idle shutdown counts from the last action");
  }
}
