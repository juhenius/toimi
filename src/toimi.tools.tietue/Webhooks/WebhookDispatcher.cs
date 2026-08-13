using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Webhooks;

/// <summary>
/// Runs accepted webhook firings after their 202 (the doorbell contract: accept fast,
/// dispatch on our own time). Each firing gets a fresh scope — the request's scoped
/// services died with the response, and PostgresTickLock pins its scope's connection.
/// </summary>
public class WebhookDispatcher(IServiceScopeFactory scopeFactory, WebhookDispatchChannel queue, ILogger<WebhookDispatcher> logger) : BackgroundService
{
  // A scheduler tick holds the advisory lock across its handler runs, and a message-kind
  // handler is an agent run bounded at minutes — the retry window must outlast the longest
  // legitimate tick (24 × 15 s ≈ 6 min), or a 202-accepted firing silently vanishes.
  public const int BusyAttempts = 24;
  private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(15);

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    logger.LogInformation("Tietue webhook dispatcher started.");
    await foreach (var firing in queue.Reader.ReadAllAsync(stoppingToken))
    {
      try
      {
        using var scope = scopeFactory.CreateScope();
        await ProcessAsync(
          firing,
          scope.ServiceProvider.GetRequiredService<TietueDbContext>(),
          scope.ServiceProvider.GetRequiredService<OccurrenceRunner>(),
          scope.ServiceProvider.GetService<ITickLock>(),
          DateTimeOffset.UtcNow,
          logger,
          stoppingToken,
          BusyRetryDelay);
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        logger.LogError(ex, "Error dispatching webhook firing for trigger {TriggerId}.", firing.TriggerId);
      }
    }
  }

  /// <summary>
  /// A 202 was a doorbell, not a promise: a trigger/entity deleted or disabled since the
  /// call, or a claim that stays Busy past the bounded retries, is logged and dropped.
  /// </summary>
  public static async Task ProcessAsync(
    WebhookFiring firing, TietueDbContext db, OccurrenceRunner runner, ITickLock? tickLock,
    DateTimeOffset now, ILogger logger, CancellationToken ct, TimeSpan? busyRetryDelay = null)
  {
    var trigger = await db.Triggers.FirstOrDefaultAsync(t => t.Id == firing.TriggerId, ct);
    if (trigger is null || !trigger.Enabled)
    {
      if (logger.IsEnabled(LogLevel.Information))
      {
        logger.LogInformation("Webhook firing dropped: trigger {TriggerId} is gone or disabled.", firing.TriggerId);
      }

      return;
    }

    var entity = await db.Entities.FirstOrDefaultAsync(e => e.Id == trigger.EntityId, ct);
    if (entity is null)
    {
      if (logger.IsEnabled(LogLevel.Information))
      {
        logger.LogInformation("Webhook firing dropped: entity {EntityId} for trigger {TriggerId} is gone.", trigger.EntityId, trigger.Id);
      }

      return;
    }

    // The tick lock serializes the claim against scheduler ticks (same contract as
    // run_trigger); NextFireAt/LastFiredAt/Enabled are never touched — advancing is
    // the scheduler's protocol and would disable a call-anchored trigger.
    for (var attempt = 1; attempt <= BusyAttempts; attempt++)
    {
      var outcome = await runner.RunAsync(trigger, entity, firing.OccurrenceUtc, now, claimLock: tickLock, @params: firing.Params, ct: ct);
      if (outcome.State != OccurrenceState.Busy)
      {
        if (logger.IsEnabled(LogLevel.Information))
        {
          logger.LogInformation("Webhook firing for trigger {TriggerId} finished: {State} ({Status}).", trigger.Id, outcome.State, outcome.Status);
        }

        return;
      }

      if (attempt < BusyAttempts)
      {
        await Task.Delay(busyRetryDelay ?? BusyRetryDelay, ct);
      }
    }

    logger.LogWarning("Webhook firing for trigger {TriggerId} dropped: run lock stayed busy after {Attempts} attempts.", trigger.Id, BusyAttempts);
  }
}
