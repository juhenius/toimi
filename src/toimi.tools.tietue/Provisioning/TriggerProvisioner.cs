using System.Text.Json;
using System.Text.Json.Nodes;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Provisioning;

public class TriggerProvisioner(TriggerRepository triggers)
{
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
        continue;
      }

      var handler = template["handler"]?.AsObject();
      var kind = handler?["kind"]?.GetValue<string>();
      if (string.IsNullOrEmpty(kind))
      {
        continue;
      }

      var config = handler?["config"]?.ToJsonString();
      await triggers.CreateAsync(entity.Id, schedule, kind, config, now, ct: ct);
    }
  }

  private static string? BuildSchedule(JsonObject? when, JsonDocument data)
  {
    if (when is null)
    {
      return null;
    }

    var atField = when["atField"]?.GetValue<string>();
    if (atField is null || !data.RootElement.TryGetProperty(atField, out var atVal) || atVal.ValueKind != JsonValueKind.String)
    {
      return null;
    }

    var at = atVal.GetString();
    var rruleField = when["rruleField"]?.GetValue<string>();
    var tzField = when["tzField"]?.GetValue<string>();

    var hasRrule = rruleField is not null && data.RootElement.TryGetProperty(rruleField, out var rr)
      && rr.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(rr.GetString());

    if (hasRrule)
    {
      var rrule = data.RootElement.GetProperty(rruleField!).GetString();
      var tz = tzField is not null && data.RootElement.TryGetProperty(tzField, out var tzv) && tzv.ValueKind == JsonValueKind.String
        ? tzv.GetString() : null;
      var obj = new JsonObject { ["start"] = at, ["rrule"] = rrule };
      if (tz is not null)
      {
        obj["tz"] = tz;
      }

      return obj.ToJsonString();
    }

    return new JsonObject { ["at"] = at }.ToJsonString();
  }
}
