using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Provisioning;

public class TriggerProvisioner(TriggerRepository triggers, ILogger<TriggerProvisioner>? logger = null)
{
  private readonly ILogger<TriggerProvisioner> _logger = logger ?? NullLogger<TriggerProvisioner>.Instance;

  public async Task ProvisionAsync(Entity entity, string? defaultTriggersJson, DateTimeOffset now, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(defaultTriggersJson))
    {
      return;
    }

    JsonNode? templates;
    try
    {
      templates = JsonNode.Parse(defaultTriggersJson);
    }
    catch (JsonException)
    {
      return;
    }

    if (templates is not JsonArray arr)
    {
      return;
    }

    foreach (var template in arr.OfType<JsonObject>())
    {
      var schedule = BuildSchedule(template["when"]?.AsObject(), entity.Data);
      if (schedule is null)
      {
        continue; // no (parseable) atField value on this entity — by design, no trigger
      }

      var handler = template["handler"]?.AsObject();
      var kind = handler?["kind"]?.GetValue<string>();
      if (string.IsNullOrEmpty(kind))
      {
        continue;
      }

      var config = handler?["config"]?.ToJsonString();
      try
      {
        await triggers.CreateAsync(entity.Id, schedule, kind, config, now, ct: ct);
      }
      catch (TietueValidationException ex)
      {
        // Entity data produced an invalid or already-exhausted schedule (e.g. a spent COUNT
        // rrule). The entity create must survive; the skip is logged, not silent.
        _logger.LogWarning("Skipped default '{Kind}' trigger for entity {EntityId} ({Type}): {Errors}",
          kind, entity.Id, entity.Type, string.Join("; ", ex.Errors));
      }
    }
  }

  private static Schedule? BuildSchedule(JsonObject? when, JsonDocument data)
  {
    if (when is null)
    {
      return null;
    }

    // A webhook template is literal anchor content, not a field reference: every entity of
    // the type gets a call-anchored trigger unconditionally (CreateAsync mints its secret).
    if (when["webhook"] is JsonObject webhook)
    {
      return Schedule.Parse(new JsonObject { ["webhook"] = webhook.DeepClone() }.ToJsonString());
    }

    var atField = when["atField"]?.GetValue<string>();
    if (atField is null || !data.RootElement.TryGetProperty(atField, out var atVal) || atVal.ValueKind != JsonValueKind.String)
    {
      return null;
    }

    if (!DateTimeOffset.TryParse(atVal.GetString(), CultureInfo.InvariantCulture,
      DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
    {
      return null; // garbage date in the entity's field — no trigger (was: a disabled zombie row)
    }

    var rruleField = when["rruleField"]?.GetValue<string>();
    var rrule = rruleField is not null && data.RootElement.TryGetProperty(rruleField, out var rr)
      && rr.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(rr.GetString())
        ? rr.GetString()
        : null;
    if (rrule is null)
    {
      return Schedule.OneShotAt(at);
    }

    var tzField = when["tzField"]?.GetValue<string>();
    var tz = tzField is not null && data.RootElement.TryGetProperty(tzField, out var tzv) && tzv.ValueKind == JsonValueKind.String
      ? tzv.GetString()
      : null;
    return Schedule.Recurring(at, rrule, tz);
  }
}
