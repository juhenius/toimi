using toimi.tools.tietue.Provisioning;

namespace toimi.tools.tietue.Behaviors;

/// <summary>
/// Re-arms the expiry trigger whenever Data changes. Runs even when the type no
/// longer has an Expiry config — the reconciler's first act removes stale expiry
/// triggers, which is how removing the behavior (or the field) disarms expiry.
/// </summary>
public sealed class ExpiryBehavior(ExpiryReconciler reconciler) : IEntityBehavior
{
  public async Task OnSavedAsync(BehaviorContext ctx, CancellationToken ct)
  {
    if (ctx.Operation == EntityOperation.Delete || !ctx.DataChanged)
    {
      return;
    }

    await reconciler.ReconcileAsync(ctx.Entity, ctx.Behaviors.Expiry, ctx.Now, ct);
  }
}
