using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Semantic;

public record ReconcileResult(int MissingEnqueued, int OrphansEnqueued);

public static class SemanticReconciler
{
  /// <summary>
  /// Diffs Postgres entities of a type against the Qdrant collection and enqueues
  /// outbox ops to repair the difference. Content mismatches are undetectable
  /// without stored hashes; this covers missing vectors and orphaned points.
  /// </summary>
  public static async Task<ReconcileResult> ReconcileAsync(TietueDbContext db, ISemanticIndex index, string type, CancellationToken ct)
  {
    var typeDef = await db.TypeDefinitions.AsNoTracking().FirstOrDefaultAsync(t => t.Name == type, ct);
    if (BehaviorSpec.SemanticIndexOf(typeDef?.Behaviors) is null)
    {
      throw new InvalidOperationException($"Type '{type}' is not semantically indexed.");
    }

    var deadRows = await db.IndexOutbox
      .Where(o => o.Type == type && o.Attempts >= SemanticOutbox.MaxAttempts)
      .ToListAsync(ct);
    db.IndexOutbox.RemoveRange(deadRows);

    // Read order matters: scroll Qdrant BEFORE the DB. An entity created during the
    // scan then shows up as "missing" (harmless idempotent re-index) instead of its
    // live vector being classified as an orphan and deleted.
    var pointIds = await index.ListIdsAsync(type, ct);
    var dbIds = await db.Entities.Where(e => e.Type == type).Select(e => e.Id).ToListAsync(ct);
    var now = DateTimeOffset.UtcNow;

    var missing = dbIds.Except(pointIds).ToList();
    var orphans = pointIds.Except(dbIds).ToList();

    foreach (var id in missing)
    {
      db.IndexOutbox.Add(new IndexOutbox { Id = Guid.NewGuid(), EntityId = id, Type = type, Op = "upsert", CreatedAt = now });
    }

    foreach (var id in orphans)
    {
      db.IndexOutbox.Add(new IndexOutbox { Id = Guid.NewGuid(), EntityId = id, Type = type, Op = "delete", CreatedAt = now });
    }

    await db.SaveChangesAsync(ct);
    return new ReconcileResult(missing.Count, orphans.Count);
  }
}
