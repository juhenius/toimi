using System.Text.Json.Nodes;
using toimi.tools.tietue.Agents;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Notifications;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Scripts;

public class ScriptEffectApplier(EntityRepository entities, INotifier notifier, TriggerRepository triggers, IAgentRunner runner)
{
  public async Task<IReadOnlyList<string>> ApplyAsync(Entity entity, ScriptEffects effects, string[] capabilities, CancellationToken ct = default)
  {
    var granted = capabilities.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var applied = new List<string>();

    if (effects.SetField is { } sf && granted.Contains("setField"))
    {
      var data = JsonNode.Parse(entity.Data.RootElement.GetRawText())!.AsObject();
      data[sf.Path] = JsonNode.Parse(sf.ValueJson);
      await entities.UpdateAsync(entity.Id, data, null, ct);
      applied.Add("setField");
    }

    if (effects.Notify is { } n && granted.Contains("notify"))
    {
      await notifier.SendAsync(n.Message, n.Title, n.Priority ?? "default", null, ct);
      applied.Add("notify");
    }

    if (effects.Trigger is { } t && granted.Contains("trigger"))
    {
      await triggers.CreateAsync(entity.Id, t.ScheduleJson, t.HandlerKind, t.HandlerConfigJson, DateTimeOffset.UtcNow, ct: ct);
      applied.Add("trigger");
    }

    if (effects.Escalate is { } prompt && granted.Contains("escalate"))
    {
      await runner.RunAsync(entity, prompt, ct);
      applied.Add("escalate");
    }

    return applied;
  }
}
