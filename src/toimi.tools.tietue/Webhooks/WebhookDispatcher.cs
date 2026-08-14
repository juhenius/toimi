using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Webhooks;

/// <summary>
/// Runs accepted webhook firings after their 202 (the doorbell contract: accept fast,
/// dispatch on our own time). Bounded-parallel consumers (Webhooks:DispatchConcurrency)
/// so one slow firing — a message handler is an agent run bounded in minutes — cannot
/// head-of-line block unrelated webhooks. Each firing gets a fresh scope — the request's
/// scoped services died with the response, and PostgresTickLock pins its scope's connection.
/// </summary>
public class WebhookDispatcher(
  IServiceScopeFactory scopeFactory,
  WebhookDispatchChannel queue,
  WebhookOptions options,
  ILogger<WebhookDispatcher> logger) : BackgroundService
{
  // A claim stays Busy for as long as a scheduler tick holds the advisory lock — and a
  // tick runs its due handlers inline, so several message-kind (agent run) triggers can
  // legitimately hold it for many minutes. The retry window must outlast the longest
  // legitimate tick or a 202-accepted firing silently vanishes, so it is measured in
  // tens of minutes (Webhooks:BusyRetryWindowMinutes), not seconds. BusyAttempts is the
  // parameterless-test default; the running dispatcher derives attempts from options.
  public const int BusyAttempts = 24;
  private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(15);

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    var concurrency = Math.Max(1, options.DispatchConcurrency);
#pragma warning disable CA1873
    logger.LogInformation("Tietue webhook dispatcher started ({Concurrency} consumers).", concurrency);
#pragma warning restore CA1873
    await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => ConsumeAsync(stoppingToken)));
  }

  private async Task ConsumeAsync(CancellationToken stoppingToken)
  {
    var busyAttempts = Math.Max(1,
      (int)(TimeSpan.FromMinutes(options.BusyRetryWindowMinutes).Ticks / BusyRetryDelay.Ticks));
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
          BusyRetryDelay,
          busyAttempts);
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        logger.LogError(ex, "Error dispatching webhook firing for trigger {TriggerId}.", firing.TriggerId);
      }
    }
  }

  /// <summary>
  /// A 202 was a doorbell, not a promise: a trigger/entity deleted, disabled, or no longer
  /// call-anchored since the call, or a claim that stays Busy past the bounded retries,
  /// is logged and dropped.
  /// </summary>
  public static async Task ProcessAsync(
    WebhookFiring firing, TietueDbContext db, OccurrenceRunner runner, ITickLock? tickLock,
    DateTimeOffset now, ILogger logger, CancellationToken ct, TimeSpan? busyRetryDelay = null,
    int? busyAttempts = null)
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

    // Anchor-swap revocation: update_trigger swapping webhook→time nulls the secret
    // ("swapping away revokes it") but leaves the trigger enabled — a queued firing
    // must honor the revocation, not run the handler under the new time anchor.
    if (Schedule.Parse(trigger.Schedule)?.Webhook is null)
    {
      if (logger.IsEnabled(LogLevel.Information))
      {
        logger.LogInformation("Webhook firing dropped: trigger {TriggerId} is no longer call-anchored.", trigger.Id);
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
    var attempts = busyAttempts ?? BusyAttempts;
    for (var attempt = 1; attempt <= attempts; attempt++)
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

      if (attempt < attempts)
      {
        await Task.Delay(busyRetryDelay ?? BusyRetryDelay, ct);
      }
    }

    logger.LogWarning("Webhook firing for trigger {TriggerId} dropped: run lock stayed busy after {Attempts} attempts.", trigger.Id, attempts);
  }
}
