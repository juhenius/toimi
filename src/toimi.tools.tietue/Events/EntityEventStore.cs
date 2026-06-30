using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Events;

public class EntityEventStore(TietueDbContext db)
{
  public async Task RecordAsync(Guid entityId, DateTimeOffset occurrenceUtc, string kind, string status, string? result, CancellationToken ct = default)
  {
    db.EntityEvents.Add(new EntityEvent
    {
      Id = Guid.NewGuid(),
      EntityId = entityId,
      OccurrenceUtc = occurrenceUtc,
      Kind = kind,
      Status = status,
      Result = result,
      CreatedAt = DateTimeOffset.UtcNow,
    });
    await db.SaveChangesAsync(ct);
  }

  public Task<bool> HasEventAsync(Guid entityId, DateTimeOffset occurrenceUtc, string kind, CancellationToken ct = default)
  {
    return db.EntityEvents.AnyAsync(e => e.EntityId == entityId && e.OccurrenceUtc == occurrenceUtc && e.Kind == kind, ct);
  }

  public Task<bool> OccurrenceHandledAsync(Guid entityId, DateTimeOffset occurrenceUtc, string kind, CancellationToken ct = default)
  {
    return db.EntityEvents.AnyAsync(e => e.EntityId == entityId && e.OccurrenceUtc == occurrenceUtc && (e.Kind == kind || e.Kind == "complete"), ct);
  }

  public async Task CompleteAsync(Guid entityId, DateTimeOffset occurrenceUtc, CancellationToken ct = default)
  {
    if (!await HasEventAsync(entityId, occurrenceUtc, "complete", ct))
    {
      await RecordAsync(entityId, occurrenceUtc, "complete", "done", null, ct);
    }
  }
}
