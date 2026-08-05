using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Semantic;

/// <summary>
/// Durable Qdrant-indexing intent. Rows are enqueued in the same SaveChanges as the
/// entity mutation (atomic), drained inline on the happy path for freshness, and
/// retried by OutboxWorker on failure.
/// </summary>
public class SemanticOutbox(TietueDbContext db, ISemanticIndex index, ILogger<SemanticOutbox>? logger = null)
{
  public const int MaxAttempts = 8;

  /// <summary>Adds an outbox row to the current change set. Caller's SaveChanges commits it with the entity.</summary>
  public IndexOutbox Enqueue(Entity entity, string op)
  {
    var row = new IndexOutbox
    {
      Id = Guid.NewGuid(),
      EntityId = entity.Id,
      Type = entity.Type,
      Op = op,
      CreatedAt = DateTimeOffset.UtcNow,
    };
    db.IndexOutbox.Add(row);
    return row;
  }

  /// <summary>
  /// Post-commit fast path: process once; on failure leave the row for the worker.
  /// Never throws — the caller's mutation already committed, so a drain problem
  /// must not surface as a failure of the create/update/delete.
  /// </summary>
  public async Task DrainAsync(IndexOutbox? row, CancellationToken ct = default)
  {
    if (row is null)
    {
      return;
    }

    try
    {
      await ProcessAsync(row, ct);
    }
    catch (Exception ex)
    {
      try
      {
        row.Attempts++;
        row.LastError = ex.Message;
        row.LastAttemptAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
      }
      catch (Exception saveEx)
      {
        // Row stays committed with Attempts=0; the worker's grace window picks it up.
        logger?.LogWarning(saveEx, "Failed to record inline-drain failure for entity {EntityId}.", row.EntityId);
      }

      logger?.LogWarning(ex, "Inline {Op} index for entity {EntityId} failed; queued for retry.", row.Op, row.EntityId);
      return;
    }

    try
    {
      db.IndexOutbox.Remove(row);
      await db.SaveChangesAsync(CancellationToken.None);
    }
    catch (Exception ex)
    {
      // Index succeeded but the row couldn't be removed: worker will re-process,
      // which is an idempotent re-index. Log and swallow.
      logger?.LogWarning(ex, "Indexed entity {EntityId} but failed to remove its outbox row; worker will re-process.", row.EntityId);
    }
  }

  /// <summary>Idempotent op execution: upsert re-reads current entity state (newest wins); missing entity = success.</summary>
  public async Task ProcessAsync(IndexOutbox row, CancellationToken ct = default)
  {
    if (row.Op == "delete")
    {
      await index.RemoveAsync(row.Type, row.EntityId, ct);
      return;
    }

    var entity = await db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Id == row.EntityId, ct);
    if (entity is null)
    {
      return; // deleted since enqueue; the delete op owns the vector removal
    }

    var typeDef = await db.TypeDefinitions.AsNoTracking().FirstOrDefaultAsync(t => t.Name == row.Type, ct);
    var cfg = TypeBehaviors.Parse(typeDef?.Behaviors).SemanticIndex;
    if (cfg is null)
    {
      return; // behavior removed since enqueue
    }

    await index.EnsureCollectionAsync(row.Type, ct);
    await index.IndexAsync(row.Type, row.EntityId, SemanticText.Extract(entity.Data, cfg.Fields), ct);
  }
}
