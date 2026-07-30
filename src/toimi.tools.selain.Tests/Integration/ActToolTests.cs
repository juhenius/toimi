using toimi.tools.selain.Tools;
using Xunit;

namespace toimi.tools.selain.Tests.Integration;

[Collection("selain")]
public class ActToolTests(SelainFixture fx)
{
  private BrowseTools Browse => new(fx.Options, fx.Policy, fx.Tabs, fx.Host);
  private ActTools Act => new(fx.Options, fx.Tabs, fx.Host);

  private static string FirstRefMatching(string snapshot, string nearText)
  {
    // Find the snapshot line mentioning nearText and extract its [ref=eN].
    foreach (var line in snapshot.Split('\n'))
    {
      if (line.Contains(nearText, StringComparison.OrdinalIgnoreCase) && line.Contains("[ref="))
      {
        var start = line.IndexOf("[ref=", StringComparison.Ordinal) + "[ref=".Length;
        return line[start..line.IndexOf(']', start)];
      }
    }
    throw new InvalidOperationException($"No ref found near '{nearText}' in snapshot:\n{snapshot}");
  }

  /// <summary>
  /// Isolation cleanup: tests share the collection's TabManager, and actions
  /// (popups) may open extra tabs — close every tab so no test couples on
  /// leftover tabs or LastShownHash state from a prior test.
  /// </summary>
  private async Task CloseAllTabsAsync()
  {
    foreach (var tab in fx.Tabs.List())
    {
      await fx.Tabs.CloseAsync(tab.Id);
    }
  }

  [ChromiumFact]
  public async Task Type_and_click_round_trip_mutates_the_page()
  {
    try
    {
      var snapshot = await Browse.Browse($"{fx.BaseUrl}/form?t=type-click");
      var nameRef = FirstRefMatching(snapshot, "Your name");
      var buttonRef = FirstRefMatching(snapshot, "Send");

      await Act.Type(nameRef, "jari", pressEnter: false);
      var afterClick = await Act.Click(buttonRef);

      Assert.Contains("jari/a", afterClick);
    }
    finally
    {
      await CloseAllTabsAsync();
    }
  }

  [ChromiumFact]
  public async Task Select_option_changes_the_selection()
  {
    try
    {
      var snapshot = await Browse.Browse($"{fx.BaseUrl}/form?t=select-option");
      var selectRef = FirstRefMatching(snapshot, "Pick one");
      var buttonRef = FirstRefMatching(snapshot, "Send");

      await Act.SelectOption(selectRef, "b");
      var after = await Act.Click(buttonRef);
      Assert.Contains("/b", after);
    }
    finally
    {
      await CloseAllTabsAsync();
    }
  }

  [ChromiumFact]
  public async Task Select_option_by_visible_label_changes_the_selection()
  {
    try
    {
      var snapshot = await Browse.Browse($"{fx.BaseUrl}/form?t=select-label");
      var selectRef = FirstRefMatching(snapshot, "Pick one");
      var buttonRef = FirstRefMatching(snapshot, "Send");

      await Act.SelectOption(selectRef, "Beta");
      var after = await Act.Click(buttonRef);
      Assert.Contains("/b", after);
    }
    finally
    {
      await CloseAllTabsAsync();
    }
  }

  [ChromiumFact]
  public async Task Select_option_without_a_match_reports_no_option()
  {
    try
    {
      var snapshot = await Browse.Browse($"{fx.BaseUrl}/form?t=select-no-match");
      var selectRef = FirstRefMatching(snapshot, "Pick one");

      var result = await Act.SelectOption(selectRef, "zzz");
      Assert.Contains("No option with value or label \"zzz\"", result);
    }
    finally
    {
      await CloseAllTabsAsync();
    }
  }

  [ChromiumFact]
  public async Task Stale_ref_reports_take_a_new_snapshot()
  {
    try
    {
      await Browse.Browse($"{fx.BaseUrl}/form?t=stale-ref");
      var result = await Act.Click("e9999");
      Assert.Contains("not found", result);
      Assert.Contains("snapshot", result);
    }
    finally
    {
      await CloseAllTabsAsync();
    }
  }

  [ChromiumFact]
  public async Task Hover_reveals_hover_content()
  {
    try
    {
      var snapshot = await Browse.Browse($"{fx.BaseUrl}/hover?t=hover");
      var menuRef = FirstRefMatching(snapshot, "Menu");
      await Act.Hover(menuRef);
      var text = await Browse.ReadPage();
      Assert.Contains("revealed-by-hover", text);
    }
    finally
    {
      await CloseAllTabsAsync();
    }
  }

  [ChromiumFact]
  public async Task Dialogs_are_auto_dismissed_and_reported()
  {
    try
    {
      var snapshot = await Browse.Browse($"{fx.BaseUrl}/dialog?t=dialog");
      var buttonRef = FirstRefMatching(snapshot, "Alert me");
      var result = await Act.Click(buttonRef);
      Assert.Contains("dialog auto-dismissed", result);
      Assert.Contains("hello from dialog", result);
    }
    finally
    {
      await CloseAllTabsAsync();
    }
  }

  [ChromiumFact]
  public async Task Popup_click_adopts_the_new_tab()
  {
    try
    {
      var snapshot = await Browse.Browse($"{fx.BaseUrl}/popup?t=popup");
      // Captured after Browse: cleanup empties the tab list, so counting before
      // Browse would let the browse tab alone satisfy the assertion.
      var before = fx.Tabs.Count;
      var linkRef = FirstRefMatching(snapshot, "open popup");
      await Act.Click(linkRef);

      // Adoption happens on the context's Page event — poll briefly instead of
      // a fixed sleep so a slow popup doesn't flake the test.
      var deadline = DateTime.UtcNow.AddSeconds(5);
      while (fx.Tabs.Count <= before && DateTime.UtcNow < deadline)
      {
        await Task.Delay(50);
      }

      Assert.True(fx.Tabs.Count > before, "popup page was not adopted as a tab");
    }
    finally
    {
      await CloseAllTabsAsync();
    }
  }

  [ChromiumFact]
  public async Task Read_page_sees_js_rendered_content_that_fetch_cannot()
  {
    try
    {
      await Browse.Browse($"{fx.BaseUrl}/js?t=js-hydrate");
      await Act.WaitFor("Hydrated content arrived", 10);
      var text = await Browse.ReadPage();
      Assert.Contains("Hydrated content arrived", text);
    }
    finally
    {
      await CloseAllTabsAsync();
    }
  }

  [ChromiumFact]
  public async Task Private_host_subresources_are_aborted_by_the_route_guard()
  {
    try
    {
      await Browse.Browse($"{fx.BaseUrl}/subres?t=subres");
      await Act.WaitFor("subresource-blocked", 10);
      var text = await Browse.ReadPage();
      Assert.Contains("subresource-blocked", text);
    }
    finally
    {
      await CloseAllTabsAsync();
    }
  }

  [ChromiumFact]
  public async Task Go_back_returns_to_the_previous_page()
  {
    try
    {
      await Browse.Browse($"{fx.BaseUrl}/static?t=go-back");
      await Browse.Browse($"{fx.BaseUrl}/form?t=go-back");
      var result = await Act.GoBack();
      Assert.Contains("/static", result);
    }
    finally
    {
      await CloseAllTabsAsync();
    }
  }
}
