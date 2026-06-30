namespace toimi.tools.tietue.Scheduling;

public class TriggerWorker(IServiceScopeFactory scopeFactory, ILogger<TriggerWorker> logger) : BackgroundService
{
  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    logger.LogInformation("Tietue trigger worker started.");
    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        using var scope = scopeFactory.CreateScope();
        var tick = scope.ServiceProvider.GetRequiredService<SchedulerTick>();
        await tick.RunDueAsync(DateTimeOffset.UtcNow, stoppingToken);
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "Error in tietue trigger worker loop.");
      }

      await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
    }
  }
}
