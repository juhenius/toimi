using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Handlers;

namespace toimi.tools.tietue.Scheduling;

public enum OccurrenceState
{
  Ran,            // handler ran and reported a status; event finalized
  Errored,        // handler threw; error event finalized with a capped message
  AlreadyHandled, // terminal or 'complete' event exists; handler not run
  InProgress,     // another instance holds a fresh claim; handler not run
  UnknownKind,    // no handler registered; error event finalized
  EntityDeleted,  // handler deleted the entity; claim row cascade-deleted, nothing finalized
  Busy,           // claim lock unavailable after bounded retries; nothing claimed
}

public record OccurrenceOutcome(OccurrenceState State, string? Status = null, string? ResultJson = null)
{
  /// <summary>
  /// True when the occurrence is settled and the trigger may advance. InProgress must stay
  /// due for retry; EntityDeleted's trigger was cascade-deleted; Busy never claimed at all.
  /// </summary>
  public bool ShouldAdvance => State is OccurrenceState.Ran or OccurrenceState.Errored
    or OccurrenceState.AlreadyHandled or OccurrenceState.UnknownKind;
}

/// <summary>
/// Owns the occurrence-execution protocol shared by the scheduler and run_trigger:
/// claim → resolve handler → dispatch → capped error capture → deleted-entity guard →
/// finalize. Callers decide only what to do with the outcome (advance the trigger,
/// format a tool response).
/// </summary>
public class OccurrenceRunner(TietueDbContext db, HandlerRegistry handlers, EntityEventStore events,
  ILogger<OccurrenceRunner>? logger = null, TimeSpan? claimLockRetryDelay = null)
{
  public const int MaxErrorMessageChars = 1000;
  private const int ClaimLockAttempts = 3;
  private const string NoHandlerResultJson = /*lang=json,strict*/ """{"error":"no handler registered"}""";

  private readonly ILogger<OccurrenceRunner> _logger = logger ?? NullLogger<OccurrenceRunner>.Instance;
  private readonly TimeSpan _claimLockRetryDelay = claimLockRetryDelay ?? TimeSpan.FromMilliseconds(500);

  public async Task<OccurrenceOutcome> RunAsync(Trigger trigger, Entity entity, DateTimeOffset occurrence, DateTimeOffset now, ITickLock? claimLock = null, CancellationToken ct = default)
  {
    var claim = await ClaimAsync(trigger, occurrence, now, claimLock, ct);
    if (claim is null)
    {
      return new OccurrenceOutcome(OccurrenceState.Busy);
    }

    if (claim == ClaimResult.InProgress)
    {
      return new OccurrenceOutcome(OccurrenceState.InProgress);
    }

    if (claim == ClaimResult.AlreadyHandled)
    {
      return new OccurrenceOutcome(OccurrenceState.AlreadyHandled);
    }

    var handler = handlers.Resolve(trigger.HandlerKind);
    if (handler is null)
    {
      _logger.LogWarning("No handler registered for kind {HandlerKind} (trigger {TriggerId}, entity {EntityId}); occurrence recorded as error.",
        trigger.HandlerKind, trigger.Id, trigger.EntityId);
      await events.FinalizeAsync(trigger.EntityId, occurrence, trigger.HandlerKind, "error", NoHandlerResultJson, ct);
      return new OccurrenceOutcome(OccurrenceState.UnknownKind, "error", NoHandlerResultJson);
    }

    var state = OccurrenceState.Ran;
    string status;
    string? resultJson;
    try
    {
      var result = await handler.HandleAsync(new HandlerContext(entity, trigger.HandlerConfig, occurrence), ct);
      status = result.Status;
      resultJson = result.Result;
    }
    catch (Exception ex)
    {
      state = OccurrenceState.Errored;
      status = "error";
      // Generic insurance: any handler's exception message lands in a jsonb column.
      var message = ex.Message.Length > MaxErrorMessageChars
        ? ex.Message[..MaxErrorMessageChars] + "… [truncated]"
        : ex.Message;
      resultJson = JsonSerializer.Serialize(new { error = message });
      _logger.LogError(ex, "Handler {HandlerKind} failed for trigger {TriggerId} (entity {EntityId}).",
        trigger.HandlerKind, trigger.Id, trigger.EntityId);
    }

    if (_logger.IsEnabled(LogLevel.Information))
    {
      _logger.LogInformation("Trigger {TriggerId} ({HandlerKind}) fired for entity {EntityId}: {Status}",
        trigger.Id, trigger.HandlerKind, trigger.EntityId, status);
    }

    // The handler may have deleted the entity (delete handler, or an agent run). The claim
    // row was cascade-deleted with it, so there is nothing to finalize — and the caller
    // must not touch the trigger either (cascade-deleted too).
    if (!await db.Entities.AnyAsync(e => e.Id == trigger.EntityId, ct))
    {
      return new OccurrenceOutcome(OccurrenceState.EntityDeleted, status, resultJson);
    }

    await events.FinalizeAsync(trigger.EntityId, occurrence, trigger.HandlerKind, status, resultJson, ct);
    return new OccurrenceOutcome(state, status, resultJson);
  }

  // Returns null when the claim lock stayed denied. The lock spans ONLY the claim: the
  // complete-check + insert and the stale-claim takeover in EntityEventStore.TryClaimAsync
  // are the protocol's only non-atomic sections (see the takeover comment there). Once a
  // fresh 'started' row is committed, the unique (entity, occurrence, kind) index and the
  // staleness window protect the run — and ticks only ever claim NextFireAt occurrences,
  // never a manual 'now' one — so holding the lock through a possibly minutes-long handler
  // would only starve the scheduler.
  private async Task<ClaimResult?> ClaimAsync(Trigger trigger, DateTimeOffset occurrence, DateTimeOffset now, ITickLock? claimLock, CancellationToken ct)
  {
    if (claimLock is null)
    {
      return await events.TryClaimAsync(trigger.EntityId, occurrence, trigger.HandlerKind, now, ct);
    }

    for (var attempt = 1; attempt <= ClaimLockAttempts; attempt++)
    {
      var lease = await claimLock.TryAcquireAsync(ct);
      if (lease is not null)
      {
        await using var _ = lease;
        return await events.TryClaimAsync(trigger.EntityId, occurrence, trigger.HandlerKind, now, ct);
      }

      if (attempt < ClaimLockAttempts)
      {
        await Task.Delay(_claimLockRetryDelay, ct);
      }
    }

    return null;
  }
}
