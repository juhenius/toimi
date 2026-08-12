using System.ComponentModel;
using Microsoft.Playwright;
using ModelContextProtocol.Server;
using toimi.tools.selain.Browser;

namespace toimi.tools.selain.Tools;

[McpServerToolType]
public class ActTools(SelainOptions options, TabManager tabs, BrowserHost host)
{
  private const int ActionTimeoutMs = 10_000;

  [McpServerTool, Description("Click the element with the given snapshot ref (e.g. e5) in the active tab. Returns the resulting page snapshot.")]
  public Task<string> Click([Description("Element ref from the snapshot, e.g. e5")] string elementRef)
  {
    return WithElementAsync(elementRef, async locator =>
    {
      await locator.ClickAsync(new() { Timeout = ActionTimeoutMs });
      return null;
    }, settleAfter: true);
  }

  [McpServerTool, Description("Hover the element with the given snapshot ref — for menus/content that reveal on hover.")]
  public Task<string> Hover([Description("Element ref from the snapshot")] string elementRef)
  {
    return WithElementAsync(elementRef, async locator =>
    {
      await locator.HoverAsync(new() { Timeout = ActionTimeoutMs });
      return null;
    }, settleAfter: false);
  }

  [McpServerTool, Description("Type text into the element with the given snapshot ref. Optionally press Enter afterwards.")]
  public Task<string> Type(
    [Description("Element ref from the snapshot")] string elementRef,
    [Description("Text to type")] string text,
    [Description("Press Enter after typing (submits many forms)")] bool pressEnter = false)
  {
    return WithElementAsync(elementRef, async locator =>
    {
      await locator.FillAsync(text, new() { Timeout = ActionTimeoutMs });
      if (pressEnter)
      {
        await locator.PressAsync("Enter", new() { Timeout = ActionTimeoutMs });
      }

      return null;
    }, settleAfter: pressEnter);
  }

  [McpServerTool, Description("Select an option (by value or label) in the <select> with the given snapshot ref.")]
  public Task<string> SelectOption(
    [Description("Element ref from the snapshot")] string elementRef,
    [Description("Option value or visible label")] string value)
  {
    return WithElementAsync(elementRef, async locator =>
    {
      // The ref lookup proved the <select> exists, so read its option list once
      // and match value-then-label in-process — Playwright's own value/label
      // matching waits ActionTimeoutMs per miss (timeout-as-control-flow).
      var choices = await locator.EvaluateAsync<string[][]>(
        "el => [Array.from(el.options).map(o => o.value), Array.from(el.options).map(o => o.label)]") ?? [[], []];
      var index = Array.IndexOf(choices[0], value);
      if (index < 0)
      {
        index = Array.IndexOf(choices[1], value);
      }

      if (index < 0)
      {
        return $"No option with value or label \"{value}\" in that <select>.";
      }

      await locator.SelectOptionAsync(new SelectOptionValue { Index = index }, new() { Timeout = ActionTimeoutMs });
      return null;
    }, settleAfter: false);
  }

  [McpServerTool, Description("Press a keyboard key in the active tab (e.g. Escape, PageDown to scroll, ArrowDown, Enter).")]
  public Task<string> PressKey([Description("Key name, e.g. Escape, PageDown, Enter")] string key)
  {
    return WithPageAsync(async page =>
    {
      await page.Keyboard.PressAsync(key);
      return null;
    }, settleAfter: false);
  }

  [McpServerTool, Description("Navigate the active tab back to the previous page.")]
  public Task<string> GoBack()
  {
    return WithPageAsync(async page =>
    {
      IResponse? response;
      try
      {
        response = await page.GoBackAsync(new() { Timeout = ActionTimeoutMs, WaitUntil = WaitUntilState.Load });
      }
      catch (TimeoutException)
      {
        return "Back navigation timed out.";
      }

      return response is null ? "No previous page in this tab's history." : null;
    }, settleAfter: false);
  }

  [McpServerTool, Description("Wait for text to appear in the active tab (or just wait N seconds if no text given). Max 30 seconds. Use for slow/lazy-loading content, then take a snapshot.")]
  public Task<string> WaitFor(
    [Description("Text to wait for (optional)")] string? text = null,
    [Description("Seconds to wait (default 15, max 30)")] int? seconds = null)
  {
    var budget = Math.Clamp(seconds ?? 15, 1, 30);
    return TabGuard.WithActiveTabAsync(options, tabs, host, async active =>
    {
      var page = ((PlaywrightSession)active.Session).Page;
      if (!string.IsNullOrEmpty(text))
      {
        try
        {
          await page.GetByText(text).First.WaitForAsync(new() { Timeout = budget * 1000 });
        }
        catch (TimeoutException)
        {
          return $"Text \"{text}\" did not appear within {budget}s.";
        }
      }
      else
      {
        await Task.Delay(TimeSpan.FromSeconds(budget));
      }

      return await PageResults.ComposeGuardedAsync(tabs, host, active);
    });
  }

  /// <summary>Resolves a snapshot ref in the active tab and runs an action on it; a stale ref gets a "take a new snapshot" nudge instead of an error. A non-null action return short-circuits.</summary>
  private Task<string> WithElementAsync(string elementRef, Func<ILocator, Task<string?>> action, bool settleAfter)
  {
    return WithPageAsync(async page =>
    {
      var locator = page.Locator($"aria-ref={elementRef}");
      return await locator.CountAsync() == 0
        ? $"ref '{elementRef}' not found — the page changed; take a new snapshot."
        : await action(locator);
    }, settleAfter);
  }

  /// <summary>
  /// Guarded action pipeline: kill switch + ActionLock + no-tab check (via
  /// TabGuard), the action itself with friendly timeout/failure messages, an
  /// optional best-effort settle for actions that trigger navigation, then the
  /// standard page result. A non-null action return short-circuits.
  /// </summary>
  private Task<string> WithPageAsync(Func<IPage, Task<string?>> action, bool settleAfter)
  {
    return TabGuard.WithActiveTabAsync(options, tabs, host, async active =>
    {
      var page = ((PlaywrightSession)active.Session).Page;
      try
      {
        if (await action(page) is { } shortCircuit)
        {
          return shortCircuit;
        }
      }
      catch (TimeoutException)
      {
        return $"Action timed out after {ActionTimeoutMs / 1000}s — the element may be covered or the page busy; take a new snapshot.";
      }
      catch (PlaywrightException ex)
      {
        return $"Action failed: {ex.Message}";
      }

      if (settleAfter)
      {
        try
        {
          await page.WaitForLoadStateAsync(LoadState.Load, new() { Timeout = 5_000 });
        }
        catch (TimeoutException)
        {
          // Best-effort settle — snapshot whatever is there.
        }
      }

      return await PageResults.ComposeGuardedAsync(tabs, host, active);
    });
  }
}
