using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Events;

public enum ClaimResult
{
  Claimed,        // caller owns the occurrence and must run the handler + finalize
  InProgress,     // another instance holds a fresh claim — skip, do NOT advance the trigger
  AlreadyHandled  // terminal event or 'complete' exists — skip handler, advance the trigger
}

public class EntityEventStore(TietueDbContext db)
{
  // How long a 'started' claim suppresses duplicates before being considered abandoned.
  public static readonly TimeSpan StaleClaimAfter = TimeSpan.FromMinutes(15);

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

  public async Task<ClaimResult> TryClaimAsync(Guid entityId, DateTimeOffset occurrenceUtc, string kind, DateTimeOffset now, CancellationToken ct = default)
  {
    if (await db.EntityEvents.AnyAsync(e => e.EntityId == entityId && e.OccurrenceUtc == occurrenceUtc && e.Kind == "complete", ct))
    {
      return ClaimResult.AlreadyHandled;
    }

    var existing = await db.EntityEvents
      .FirstOrDefaultAsync(e => e.EntityId == entityId && e.OccurrenceUtc == occurrenceUtc && e.Kind == kind, ct);

    if (existing is null)
    {
      var claim = new EntityEvent
      {
        Id = Guid.NewGuid(),
        EntityId = entityId,
        OccurrenceUtc = occurrenceUtc,
        Kind = kind,
        Status = "started",
        CreatedAt = now,
      };
      db.EntityEvents.Add(claim);
      try
      {
        await db.SaveChangesAsync(ct);
        return ClaimResult.Claimed;
      }
      catch (DbUpdateException)
      {
        // Unique (entity, occurrence, kind) index or a concurrent entity delete (FK):
        // detach ONLY the failed claim row — the caller's tracked trigger batch must
        // survive. Retry-next-tick semantics are safe for both causes.
        db.Entry(claim).State = EntityState.Detached;
        return ClaimResult.InProgress;
      }
    }

    if (existing.Status != "started")
    {
      return ClaimResult.AlreadyHandled;
    }

    if (existing.CreatedAt > now - StaleClaimAfter)
    {
      return ClaimResult.InProgress;
    }

    // Abandoned claim (crashed instance): take it over and refresh the window.
    // Plain read-modify-write: safe only because every claimant serializes on the
    // Postgres advisory tick lock — SchedulerTick holds it for the whole tick and
    // run_trigger's OccurrenceRunner acquires it around this claim (+ Recreate
    // deploys). The claim table alone is NOT race-proof for stale take-overs — do
    // not remove the tick lock believing it is.
    existing.CreatedAt = now;
    await db.SaveChangesAsync(ct);
    return ClaimResult.Claimed;
  }

  public async Task FinalizeAsync(Guid entityId, DateTimeOffset occurrenceUtc, string kind, string status, string? result, CancellationToken ct = default)
  {
    var evt = await db.EntityEvents
      .FirstOrDefaultAsync(e => e.EntityId == entityId && e.OccurrenceUtc == occurrenceUtc && e.Kind == kind, ct);
    if (evt is null)
    {
      return; // entity deleted during handling — the claim row was cascade-deleted
    }

    evt.Status = status;
    evt.Result = result;
    await db.SaveChangesAsync(ct);
  }

  public async Task CompleteAsync(Guid entityId, DateTimeOffset occurrenceUtc, CancellationToken ct = default)
  {
    if (!await HasEventAsync(entityId, occurrenceUtc, "complete", ct))
    {
      await RecordAsync(entityId, occurrenceUtc, "complete", "done", null, ct);
    }
  }
}
