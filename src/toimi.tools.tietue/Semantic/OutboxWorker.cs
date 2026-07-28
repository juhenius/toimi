using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Semantic;

/// <summary>
/// Background retry loop for index outbox rows the inline drain couldn't handle.
/// Exponential backoff per row; rows at MaxAttempts are dead (surfaced via admin);
/// reconcile purges dead rows for the reconciled type and enqueues fresh repair ops.
/// </summary>
public class OutboxWorker(IServiceScopeFactory scopeFactory, ILogger<OutboxWorker> logger) : BackgroundService
{
  private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
  private static readonly TimeSpan UndrainedGrace = TimeSpan.FromMinutes(2);
  private const int BatchSize = 20;
  private const int CandidateWindow = 200;

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    logger.LogInformation("Tietue index outbox worker started.");
    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TietueDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<SemanticOutbox>();
        await RunOnceAsync(db, outbox, DateTimeOffset.UtcNow, stoppingToken, logger);
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "Error in index outbox worker loop.");
      }

      await Task.Delay(Interval, stoppingToken);
    }
  }

  // Accepted race: ProcessAsync re-reads the entity and then calls IndexAsync — not atomic
  // against a concurrent inline delete. The worker can read the entity, the delete can commit
  // and remove the vector, and the worker's IndexAsync then lands after, leaving an orphaned
  // point until reconcile sweeps it. User-invisible in the meantime: SearchAsync joins results
  // against the DB, so the orphan never surfaces.
  public static async Task<int> RunOnceAsync(
    TietueDbContext db, SemanticOutbox outbox, DateTimeOffset now,
    CancellationToken ct, ILogger? logger = null)
  {
    var candidates = await db.IndexOutbox
      .Where(o => o.Attempts < SemanticOutbox.MaxAttempts)
      .OrderBy(o => o.CreatedAt)
      .Take(CandidateWindow) // wide fetch: due-ness (backoff math) isn't SQL-translatable,
                             // and a narrow window of purely-backoff rows would starve newer due rows
      .ToListAsync(ct);

    var processed = 0;
    foreach (var row in candidates.Where(r => IsDue(r, now)).Take(BatchSize))
    {
      try
      {
        await outbox.ProcessAsync(row, ct);
        db.IndexOutbox.Remove(row);
        processed++;
      }
      catch (Exception ex)
      {
        row.Attempts++;
        row.LastError = ex.Message;
        row.LastAttemptAt = now;
        if (row.Attempts >= SemanticOutbox.MaxAttempts)
        {
          logger?.LogError(ex, "Index op {Op} for entity {EntityId} is dead after {Attempts} attempts.", row.Op, row.EntityId, row.Attempts);
        }
      }

      await db.SaveChangesAsync(ct);
    }

    return processed;
  }

  private static bool IsDue(IndexOutbox row, DateTimeOffset now)
  {
    if (row.Attempts == 0)
    {
      // Never drained inline (crash between commit and drain, or reconcile-enqueued):
      // give the inline path a grace window before the worker takes over.
      return row.CreatedAt + UndrainedGrace <= now;
    }

    return row.LastAttemptAt is null
      || row.LastAttemptAt + TimeSpan.FromMinutes(Math.Pow(2, row.Attempts)) <= now;
  }
}
