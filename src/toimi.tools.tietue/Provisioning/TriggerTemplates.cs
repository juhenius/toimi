using System.Text.Json;
using System.Text.Json.Nodes;
using toimi.tools.tietue.Handlers;

namespace toimi.tools.tietue.Provisioning;

/// <summary>
/// Structural validation of a type's DefaultTriggers templates at define_type time, over the
/// grammar TriggerProvisioner consumes. when.atField/rruleField/tzField are FIELD REFERENCES
/// resolved per-entity, so only their structure is checked here (schedule CONTENT can only be
/// validated per-entity — TriggerProvisioner logs and skips those). handler.config is literal
/// JSON ({token} placeholders are plain strings), so each handler's ValidateConfig applies
/// directly when a registry is available; without one (bare test repositories) the check is
/// structure-only.
/// </summary>
public static class TriggerTemplates
{
  public static IReadOnlyList<string> Validate(string defaultTriggersJson, HandlerRegistry? handlers)
  {
    JsonNode? root;
    try
    {
      root = JsonNode.Parse(defaultTriggersJson);
    }
    catch (JsonException ex)
    {
      return [$"Invalid default triggers JSON: {ex.Message}"];
    }

    if (root is not JsonArray arr)
    {
      return ["defaultTriggers must be a JSON array of trigger templates."];
    }

    var errors = new List<string>();
    for (var i = 0; i < arr.Count; i++)
    {
      if (arr[i] is not JsonObject template)
      {
        errors.Add($"defaultTriggers[{i}] must be an object.");
        continue;
      }

      ValidateWhen(template, i, errors);
      ValidateHandler(template, i, handlers, errors);
    }

    return errors;
  }

  private static void ValidateWhen(JsonObject template, int i, List<string> errors)
  {
    if (template["when"] is not JsonObject when)
    {
      errors.Add($"defaultTriggers[{i}].when must be an object naming an 'atField', or {{\"webhook\":{{...}}}} for call-anchored.");
      return;
    }

    if (when.ContainsKey("webhook"))
    {
      ValidateWebhookWhen(when, i, errors);
      return;
    }

    if (when["atField"] is not JsonValue at || !at.TryGetValue<string>(out var atField) || string.IsNullOrWhiteSpace(atField))
    {
      errors.Add($"defaultTriggers[{i}].when.atField must name the entity field holding the first fire time.");
    }

    foreach (var name in (string[])["rruleField", "tzField"])
    {
      if (when[name] is { } v && (v is not JsonValue value || !value.TryGetValue<string>(out _)))
      {
        errors.Add($"defaultTriggers[{i}].when.{name} must be a string field name.");
      }
    }
  }

  // Unlike atField/rruleField, when.webhook is literal anchor content (nothing to resolve
  // per-entity), so its content is fully validatable here via the Schedule grammar itself.
  private static void ValidateWebhookWhen(JsonObject when, int i, List<string> errors)
  {
    // All three time-anchor field refs are rejected, not just atField: a co-present
    // rruleField would validate clean and then be silently discarded at provision time.
    foreach (var name in (string[])["atField", "rruleField", "tzField"])
    {
      if (when.ContainsKey(name))
      {
        errors.Add($"defaultTriggers[{i}].when has exactly one anchor: 'atField' (with optional 'rruleField'/'tzField') or 'webhook', not both.");
        return;
      }
    }

    // ContainsKey is true for a JSON null, so the value must be pattern-matched — the
    // indexer returns a null JsonNode for {"webhook": null}.
    if (when["webhook"] is not JsonObject webhookContent)
    {
      errors.Add($"defaultTriggers[{i}].when.webhook must be an object with optional iso-date 'activeAfter'/'activeUntil' and integer 'rateLimit'.");
      return;
    }

    var schedule = Scheduling.Schedule.Parse(new JsonObject { ["webhook"] = webhookContent.DeepClone() }.ToJsonString());
    if (schedule is null || !schedule.IsWebhook)
    {
      errors.Add($"defaultTriggers[{i}].when.webhook must be an object with optional iso-date 'activeAfter'/'activeUntil' and integer 'rateLimit'.");
      return;
    }

    if (!schedule.TryValidate(out var error))
    {
      errors.Add($"defaultTriggers[{i}].when.webhook: {error}");
    }
  }

  private static void ValidateHandler(JsonObject template, int i, HandlerRegistry? handlers, List<string> errors)
  {
    if (template["handler"] is not JsonObject handlerNode
      || handlerNode["kind"] is not JsonValue kindValue
      || !kindValue.TryGetValue<string>(out var kind)
      || string.IsNullOrWhiteSpace(kind))
    {
      errors.Add($"defaultTriggers[{i}].handler.kind must be a handler kind string.");
      return;
    }

    if (handlers is null)
    {
      return; // structure-only context (no registry): kind/config checked at write time instead
    }

    if (handlers.Resolve(kind) is not { } handler)
    {
      errors.Add($"defaultTriggers[{i}].handler.kind '{kind}' is not a registered handler. Valid kinds: {string.Join(", ", handlers.Kinds)}.");
      return;
    }

    var result = handler.ValidateConfig(handlerNode["config"]?.ToJsonString());
    if (!result.IsValid)
    {
      errors.AddRange(result.Errors.Select(e => $"defaultTriggers[{i}].handler.config: {e}"));
    }
  }
}
