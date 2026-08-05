using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Provisioning;

public class ExpiryReconciler(TietueDbContext db, TriggerRepository triggers)
{
  public const string SourceTag = "expiry";

  public async Task ReconcileAsync(Entity entity, ExpiryConfig? cfg, DateTimeOffset now, CancellationToken ct = default)
  {
    var existing = await db.Triggers.Where(t => t.EntityId == entity.Id && t.Source == SourceTag).ToListAsync(ct);
    if (existing.Count > 0)
    {
      db.Triggers.RemoveRange(existing);
      await db.SaveChangesAsync(ct);
    }

    if (cfg is null)
    {
      return;
    }

    var at = ExpiryAt(entity.Data, cfg.Field);
    if (at is null)
    {
      return;
    }

    var schedule = new JsonObject { ["at"] = at }.ToJsonString();
    var kind = cfg.Prompt is null ? "delete" : "message";
    var config = cfg.Prompt is null ? null : MessageConfig(entity.Type, cfg.Field, cfg.Prompt);

    await triggers.CreateAsync(entity.Id, schedule, kind, config, now, SourceTag, ct);
  }

  private static string? ExpiryAt(JsonDocument data, string field)
  {
    return data.RootElement.TryGetProperty(field, out var v)
      && v.ValueKind == JsonValueKind.String
      && !string.IsNullOrWhiteSpace(v.GetString())
        ? v.GetString()
        : null;
  }

  private static string MessageConfig(string type, string field, string prompt)
  {
    var instruction =
      $"The expiry time for this '{type}' entity has arrived. Decide whether it should be removed now. "
      + "If it is no longer needed, delete it using the delete tool. "
      + $"If it is still needed, update its '{field}' field to a later time using the update tool, which re-arms expiry. "
      + $"Guidance: {prompt}";
    return new JsonObject { ["promptTemplate"] = instruction }.ToJsonString();
  }
}
