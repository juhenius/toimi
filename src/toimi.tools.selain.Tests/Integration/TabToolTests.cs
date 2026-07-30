using toimi.tools.selain.Browser;
using toimi.tools.selain.Tools;
using Xunit;

namespace toimi.tools.selain.Tests.Integration;

[Collection("selain")]
public class TabToolTests(SelainFixture fx)
{
  private TabTools Tool => new(fx.Options, fx.Policy, fx.Tabs, fx.Host);

  /// <summary>
  /// Isolation cleanup: tests share the collection's TabManager — close every
  /// tab so no test couples on leftover tabs or LastShownHash state.
  /// </summary>
  private async Task CloseAllTabsAsync()
  {
    foreach (var tab in fx.Tabs.List())
    {
      await fx.Tabs.CloseAsync(tab.Id);
    }
  }

  [ChromiumFact]
  public async Task New_list_switch_close_lifecycle()
  {
    try
    {
      var created = await Tool.Tabs("new", url: $"{fx.BaseUrl}/static?t=tab-lifecycle");
      Assert.Contains("Static page", created);

      var list = await Tool.Tabs("list");
      Assert.Contains("/tabs/", list);           // viewer URL present
      Assert.Contains("https://toimi.example", list);
      Assert.Contains("[active]", list);

      // Extract the new tab's id from the list output ("- <guid> ..." lines).
      var activeLine = list.Split('\n').First(l => l.Contains("[active]"));
      var id = activeLine.TrimStart('-', ' ').Split(' ')[0];

      Assert.Contains("Switched", await Tool.Tabs("switch", tabId: id));

      var closed = await Tool.Tabs("close", tabId: id);
      Assert.Contains("closed", closed, StringComparison.OrdinalIgnoreCase);
    }
    finally
    {
      await CloseAllTabsAsync();
    }
  }

  [ChromiumFact]
  public async Task New_tab_with_viewport_uses_the_given_size()
  {
    try
    {
      var created = await Tool.Tabs("new", url: $"{fx.BaseUrl}/static?t=tab-viewport", width: 800, height: 1280);
      Assert.Contains("800x1280", created);

      // Behavioral check: the page really renders at the requested size.
      var tab = fx.Tabs.Active;
      Assert.NotNull(tab);
      var page = ((PlaywrightSession)tab.Session).Page;
      Assert.Equal(800, await page.EvaluateAsync<int>("window.innerWidth"));
      Assert.Equal(1280, await page.EvaluateAsync<int>("window.innerHeight"));

      Assert.Contains("together", await Tool.Tabs("new", width: 800));
      Assert.Contains("200-4000", await Tool.Tabs("new", width: 50, height: 50));

      var list = await Tool.Tabs("list");
      var activeLine = list.Split('\n').First(l => l.Contains("[active]"));
      var id = activeLine.TrimStart('-', ' ').Split(' ')[0];
      await Tool.Tabs("close", tabId: id);
    }
    finally
    {
      await CloseAllTabsAsync();
    }
  }

  [ChromiumFact]
  public async Task Unknown_action_and_bad_ids_report_errors()
  {
    Assert.Contains("Unknown action", await Tool.Tabs("frobnicate"));
    Assert.Contains("not found", await Tool.Tabs("switch", tabId: Guid.NewGuid().ToString()));
    Assert.Contains("Invalid tab id", await Tool.Tabs("switch", tabId: "not-a-guid"));
  }
}
