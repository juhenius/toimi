using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Validation;
using toimi.tools.tietue.Webhooks;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class UpdateTriggerTool(TriggerRepository repository, HandlerRegistry handlers, WebhookOptions webhookOptions)
{
  [McpServerTool, Description("Update a trigger's schedule, handler config, and/or enabled flag; recurring schedules without a tz default to the server's user timezone, pass \"tz\":\"UTC\" for fixed-UTC recurrence. Swapping the anchor to {\"webhook\":{...}} mints a new capability url/secret; swapping away revokes it; editing a webhook anchor's window/rateLimit keeps the existing url.")]
  public async Task<string> UpdateTrigger(
      [Description("Trigger id (GUID)")] string id,
      [Description("New schedule spec JSON (optional)")] string? schedule = null,
      [Description("New handler config JSON (optional)")] string? handlerConfig = null,
      [Description("Enable/disable the trigger (optional)")] bool? enabled = null)
  {
    if (!Guid.TryParse(id, out var triggerId))
    {
      return "Invalid id. Expected a GUID.";
    }

    if (handlerConfig is not null)
    {
      var existing = await repository.GetAsync(triggerId);
      if (existing is null)
      {
        return $"Trigger '{id}' not found.";
      }

      // A legacy trigger whose kind no longer resolves can't be config-validated; leave it
      // to run_trigger's unknown-kind error path rather than blocking edits.
      if (handlers.Resolve(existing.HandlerKind) is { } handler)
      {
        var configCheck = handler.ValidateConfig(handlerConfig);
        if (!configCheck.IsValid)
        {
          return string.Join("; ", configCheck.Errors);
        }
      }
    }

    try
    {
      var t = await repository.UpdateAsync(triggerId, schedule, handlerConfig, enabled, DateTimeOffset.UtcNow);
      return t is null
        ? $"Trigger '{id}' not found."
        : t.Secret is null
          ? JsonSerializer.Serialize(new { id = t.Id.ToString(), enabled = t.Enabled, nextFireAt = t.NextFireAt?.ToString("o") })
          : JsonSerializer.Serialize(new
          {
            id = t.Id.ToString(),
            enabled = t.Enabled,
            nextFireAt = (string?)null,
            url = WebhookEndpoints.Url(webhookOptions, t),
            secret = t.Secret,
          });
    }
    catch (TietueValidationException ex)
    {
      return string.Join("; ", ex.Errors);
    }
  }
}
