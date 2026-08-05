using toimi.tools.tietue.Provisioning;

namespace toimi.tools.tietue.Behaviors;

/// <summary>Copy-down default triggers: stamps the type's DefaultTriggers onto each new entity. Create-time only by design.</summary>
public sealed class TriggerProvisioningBehavior(TriggerProvisioner provisioner) : IEntityBehavior
{
  public async Task OnSavedAsync(BehaviorContext ctx, CancellationToken ct)
  {
    if (ctx.Operation == EntityOperation.Create)
    {
      await provisioner.ProvisionAsync(ctx.Entity, ctx.DefaultTriggersJson, ctx.Now, ct);
    }
  }
}
