using System.Text.Json.Nodes;
using toimi.tools.tietue.Agents;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Notifications;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Scripts;

public class ScriptEffectApplier(EntityRepository entities, INotifier notifier, TriggerRepository triggers, IAgentRunner runner, Lazy<HandlerRegistry> handlers, Toimi.Core.Configuration.ToimiConfiguration config)
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
      // Same guards as SetTriggerTool: don't create a trigger the scheduler can never fire
      // (unknown handler kind logs "no handler" forever; a null-resolving schedule never fires).
      // Entity existence is implicit — the script acts on its own known entity.
      if (handlers.Value.Resolve(t.HandlerKind) is null)
      {
        applied.Add($"trigger:error:unknown handlerKind '{t.HandlerKind}'");
      }
      else if (Schedules.InitialNextFireAt(Schedules.WithDefaultTimeZone(t.ScheduleJson, config.UserTimeZone), DateTimeOffset.UtcNow) is null)
      {
        applied.Add("trigger:error:schedule does not resolve to a future fire time");
      }
      else
      {
        await triggers.CreateAsync(entity.Id, t.ScheduleJson, t.HandlerKind, t.HandlerConfigJson, DateTimeOffset.UtcNow, ct: ct);
        applied.Add("trigger");
      }
    }

    if (effects.Escalate is { } prompt && granted.Contains("escalate"))
    {
      await runner.RunAsync(entity, prompt, ct);
      applied.Add("escalate");
    }

    return applied;
  }
}
