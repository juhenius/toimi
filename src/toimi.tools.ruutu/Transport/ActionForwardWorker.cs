namespace toimi.tools.ruutu.Transport;

/// <summary>
/// Drains the action-forward queue in the background. Each forward gets a
/// fresh DI scope (the request scope died with the response) and runs
/// detached from any request token, so a shell disconnect can neither cancel
/// a forward mid-flight nor lose its outcome record.
/// </summary>
public class ActionForwardWorker(
  IServiceScopeFactory scopeFactory,
  ActionForwardChannel queue,
  ILogger<ActionForwardWorker> logger) : BackgroundService
{
  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    await foreach (var forward in queue.Reader.ReadAllAsync(stoppingToken))
    {
      try
      {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ActionForwarder>()
          .ForwardAsync(forward, stoppingToken);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
#pragma warning disable CA1031 // One bad forward must not kill the worker loop.
      catch (Exception ex)
#pragma warning restore CA1031
      {
        logger.LogError(ex, "Action forward for display '{Identifier}' failed unexpectedly.", forward.Identifier);
      }
    }
  }
}
