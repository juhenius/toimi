using toimi.tools.tietue.Data;
using toimi.tools.tietue.Semantic;

namespace toimi.tools.tietue.Behaviors;

/// <summary>
/// Wraps SemanticOutbox's enqueue/drain pair: the row is enqueued into the entity's
/// change set (durable with the mutation) and drained only after commit, so a Qdrant
/// hiccup can never roll back — or be rolled back by — the entity write.
/// </summary>
public sealed class SemanticIndexBehavior(SemanticOutbox outbox) : IEntityBehavior
{
  private const string PendingRowKey = nameof(SemanticIndexBehavior);

  public Task OnSavingAsync(BehaviorContext ctx, CancellationToken ct)
  {
    if (ctx.Behaviors.SemanticIndex is null || !ctx.DataChanged)
    {
      return Task.CompletedTask;
    }

    var op = ctx.Operation == EntityOperation.Delete ? "delete" : "upsert";
    ctx.Items[PendingRowKey] = outbox.Enqueue(ctx.Entity, op);
    return Task.CompletedTask;
  }

  public async Task OnCommittedAsync(BehaviorContext ctx, CancellationToken ct)
  {
    if (ctx.Items.TryGetValue(PendingRowKey, out var row))
    {
      await outbox.DrainAsync((IndexOutbox?)row, ct);
    }
  }
}
