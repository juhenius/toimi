using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Behaviors;

public enum EntityOperation
{
  Create,
  Update,
  Delete,
}

/// <summary>
/// Per-operation state handed to every behavior hook. One instance per repository
/// operation; <see cref="Items"/> carries behavior-private state between hooks
/// (e.g. the semantic outbox row from OnSaving to OnCommitted) — behaviors are
/// DI-scoped and a scope runs many sequential operations, so instance fields
/// would leak state across operations.
/// </summary>
public sealed class BehaviorContext
{
  public required Entity Entity { get; init; }
  public required EntityOperation Operation { get; init; }
  public required TypeBehaviors Behaviors { get; init; }
  public string? DefaultTriggersJson { get; init; }
  public required DateTimeOffset Now { get; init; }

  /// <summary>False only for a tags-only update; behaviors that react to Data skip those.</summary>
  public bool DataChanged { get; init; } = true;

  public Dictionary<string, object?> Items { get; } = [];
}

/// <summary>
/// A first-class per-type behavior. Hooks bracket EntityRepository's SaveChanges:
/// OnSaving joins the pending change set (same SaveChanges, atomic with the entity);
/// OnSaved runs after the save — on create still inside the ambient transaction, so
/// its own saves commit or roll back with the entity; OnCommitted runs after the
/// transaction is committed and disposed, and must not fail the operation.
/// </summary>
public interface IEntityBehavior
{
  Task OnSavingAsync(BehaviorContext ctx, CancellationToken ct)
  {
    return Task.CompletedTask;
  }

  Task OnSavedAsync(BehaviorContext ctx, CancellationToken ct)
  {
    return Task.CompletedTask;
  }

  Task OnCommittedAsync(BehaviorContext ctx, CancellationToken ct)
  {
    return Task.CompletedTask;
  }
}
