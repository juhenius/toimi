using Microsoft.Playwright;

namespace toimi.tools.selain.Browser;

/// <summary>
/// Lazily launches one headless Chromium + one context. Two egress guards run in
/// the browser as defense in depth (the only guards on dev clusters whose CNI
/// doesn't enforce NetworkPolicy): a route guard aborts each first-hop request
/// (top-level navigation and subresource) to a private host, and a
/// navigation-committed guard that bounces the whole page to about:blank when
/// ANY frame — main frame or subframe (a hostile page can 302 an iframe) —
/// commits an http(s) document on a disallowed host, because Playwright 1.61
/// does NOT re-fire the route handler for server-side redirect targets, so a
/// 3xx to a private host bypasses the route guard entirely. Redirect hops that
/// never commit (and WebSocket egress, which the route guard also doesn't
/// cover) still rely on the Task 14 NetworkPolicy. Crash → relaunch with a
/// one-shot restart notice; idle → torn down by IdleShutdownService.
/// </summary>
public sealed class BrowserHost(SelainOptions options, UrlPolicy policy, TabManager tabs, ILogger<BrowserHost> logger) : IAsyncDisposable
{
  private readonly SemaphoreSlim _launchLock = new(1, 1);
  private IPlaywright? _playwright;
  private IBrowser? _browser;
  private IBrowserContext? _context;
  private bool _restartNotice;
  private int _activeStreams;
  // UTC ticks in a long: DateTimeOffset is 16 bytes, so a bare property could
  // tear between a tool-thread Touch and the idle-loop read.
  private long _lastUseTicks = DateTimeOffset.UtcNow.UtcTicks;

  public DateTimeOffset LastUse => new(Interlocked.Read(ref _lastUseTicks), TimeSpan.Zero);
  public int ActiveStreams => _activeStreams;
  public bool IsRunning => _browser is { IsConnected: true };

  /// <summary>Refresh the idle clock — called on every tool action, not just context acquisition, so "idle 15 min" means 15 min since the agent last did anything.</summary>
  public void Touch()
  {
    Interlocked.Exchange(ref _lastUseTicks, DateTimeOffset.UtcNow.UtcTicks);
  }

  public void StreamStarted()
  {
    Interlocked.Increment(ref _activeStreams);
  }

  public void StreamEnded()
  {
    Interlocked.Decrement(ref _activeStreams);
  }

  /// <summary>True exactly once after a crash-relaunch; tools prepend a notice.</summary>
  public bool ConsumeRestartNotice()
  {
    var notice = _restartNotice;
    _restartNotice = false;
    return notice;
  }

  public async Task<IBrowserContext> GetContextAsync()
  {
    Touch();
    if (_browser is { IsConnected: true } && _context is not null)
    {
      return _context;
    }

    await _launchLock.WaitAsync();
    try
    {
      if (_browser is { IsConnected: true } && _context is not null)
      {
        return _context;
      }

      if (_browser is not null)
      {
        _restartNotice = true;
        tabs.ResetAll();
        try
        {
          await _browser.DisposeAsync();
        }
        catch (PlaywrightException)
        {
          // Already dead — that's why we're here.
        }
        _context = null;
      }

      _playwright ??= await Playwright.CreateAsync();
      _browser = await _playwright.Chromium.LaunchAsync(new()
      {
        Headless = true,
        Args = ["--no-sandbox", "--disable-dev-shm-usage", "--disable-gpu"]
      });
      _context = await _browser.NewContextAsync(new()
      {
        ViewportSize = new() { Width = 1280, Height = 720 }
      });

      await _context.RouteAsync("**/*", async route =>
      {
        // Playwright fires this async handler as async void; a throw here (page
        // closing mid-op, browser crash) would be unobserved and could tear the
        // pod down, so every path is guarded and swallowed.
        try
        {
          var host = Uri.TryCreate(route.Request.Url, UriKind.Absolute, out var uri) ? uri.Host : null;
          if (host is not null && policy.IsAllowedHost(host))
          {
            await route.ContinueAsync();
          }
          else
          {
            if (logger.IsEnabled(LogLevel.Warning))
            {
              logger.LogWarning("Blocked browser request to {Url} (private/internal host).", route.Request.Url);
            }
            await route.AbortAsync("blockedbyclient");
          }
        }
        catch (PlaywrightException ex)
        {
          logger.LogDebug(ex, "Route handler could not complete (page or browser gone).");
        }
      });

      _context.Page += (_, page) =>
      {
        var id = tabs.Adopt(new PlaywrightSession(page));

        page.Dialog += async (_, dialog) =>
        {
          try
          {
            tabs.NoteDialog(id, $"[{dialog.Type} dialog auto-dismissed: \"{dialog.Message}\"]");
            await dialog.DismissAsync();
          }
          catch (PlaywrightException ex)
          {
            logger.LogDebug(ex, "Dialog dismiss failed (already handled or page gone).");
          }
        };

        // Server-side redirects bypass the route guard (see class summary), so
        // re-check the host each frame actually commits to — main frame OR
        // subframe (an embedded iframe's src can 302 to an internal host) —
        // and bounce the whole page off any disallowed landing to about:blank.
        // The scheme check keeps about:blank/srcdoc/data: subframes (whose
        // hosts are empty or meaningless) from triggering bounces.
        page.FrameNavigated += async (_, frame) =>
        {
          try
          {
            if (!Uri.TryCreate(frame.Url, UriKind.Absolute, out var uri)
              || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
              || policy.IsAllowedHost(uri.Host))
            {
              return;
            }

            if (logger.IsEnabled(LogLevel.Warning))
            {
              logger.LogWarning("Blocked navigation landing on {Url} (private/internal host after redirect).", frame.Url);
            }
            tabs.NoteDialog(id, $"[blocked redirect to {uri.Host}]");

            // The disallowed document has committed; yank the page off it. This
            // navigation races the original in-flight load (which Chromium may
            // still be settling), so it can be interrupted — retry until the
            // page is actually parked on about:blank. Commit (not Load) is the
            // cheapest wait that proves the document swapped.
            for (var attempt = 0; attempt < 5 && !page.Url.StartsWith("about:", StringComparison.Ordinal); attempt++)
            {
              try
              {
                await page.GotoAsync("about:blank", new() { WaitUntil = WaitUntilState.Commit });
              }
              catch (PlaywrightException) when (attempt < 4)
              {
                // Interrupted by the competing navigation — try again.
              }
            }
          }
          catch (PlaywrightException ex)
          {
            logger.LogDebug(ex, "Redirect guard could not bounce the page (page gone).");
          }
        };

        page.Close += (_, _) => tabs.RemoveByHandle(page);
      };

      return _context;
    }
    finally
    {
      _launchLock.Release();
    }
  }

  /// <summary>Idle teardown: nothing open, nothing streaming, quiet past the threshold.</summary>
  public async Task ShutdownIfIdleAsync()
  {
    if (!IsRunning || tabs.Count > 0 || _activeStreams > 0
      || DateTimeOffset.UtcNow - LastUse < TimeSpan.FromMinutes(options.IdleShutdownMinutes))
    {
      return;
    }

    if (logger.IsEnabled(LogLevel.Information))
    {
      logger.LogInformation("Closing idle browser after {Minutes} min.", options.IdleShutdownMinutes);
    }

    await DisposeBrowserAsync();
  }

  private async Task DisposeBrowserAsync()
  {
    await _launchLock.WaitAsync();
    try
    {
      if (_browser is not null)
      {
        try
        {
          await _browser.DisposeAsync();
        }
        catch (PlaywrightException)
        {
          // Best-effort teardown.
        }
      }
      _browser = null;
      _context = null;
      tabs.ResetAll();
    }
    finally
    {
      _launchLock.Release();
    }
  }

  public async ValueTask DisposeAsync()
  {
    await DisposeBrowserAsync();
    _playwright?.Dispose();
    _launchLock.Dispose();
  }
}
