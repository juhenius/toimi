namespace toimi.tools.selain.Browser;

/// <summary>
/// Closes the browser when nothing has used it for Selain:IdleShutdownMinutes —
/// a weeks-running Chromium slowly leaks memory, and relaunch is lazy anyway.
/// ShutdownIfIdleAsync only checks state and (via BrowserHost) swallows
/// Playwright teardown failures, so a bad tick cannot kill the loop.
/// </summary>
public sealed class IdleShutdownService(BrowserHost host) : BackgroundService
{
  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
    try
    {
      while (await timer.WaitForNextTickAsync(stoppingToken))
      {
        await host.ShutdownIfIdleAsync();
      }
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
      // Host is stopping — normal exit.
    }
  }
}
