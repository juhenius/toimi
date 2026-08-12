using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Provisioning;

public class ExpiryReconciler(TietueDbContext db, TriggerRepository triggers, ILogger<ExpiryReconciler>? logger = null)
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

    if (!entity.Data.RootElement.TryGetProperty(cfg.Field, out var raw))
    {
      return; // field absent — nothing to arm
    }

    var at = ParseExpiry(raw);
    if (at is null)
    {
      // A garbage date must not arm a dead trigger — but silently skipping made
      // it look like expiry was never configured, so say why nothing armed.
      logger?.LogWarning(
        "Entity {EntityId} ({EntityType}): expiry field '{Field}' is not a parseable date; no expiry trigger armed.",
        entity.Id, entity.Type, cfg.Field);
      return;
    }

    var kind = cfg.Prompt is null ? "delete" : "message";
    var config = cfg.Prompt is null ? null : MessageConfig(entity.Type, cfg.Field, cfg.Prompt);

    await triggers.CreateAsync(entity.Id, Schedule.OneShotAt(at.Value), kind, config, now, SourceTag, ct);
  }

  private static DateTimeOffset? ParseExpiry(JsonElement value)
  {
    return value.ValueKind == JsonValueKind.String
      && DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
        ? at
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
