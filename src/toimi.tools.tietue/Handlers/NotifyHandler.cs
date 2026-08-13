using System.Text.Json;
using Toimi.Notifications;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Handlers;

public class NotifyHandler(INotifier notifier) : INativeHandler
{
  public string Kind => "notify";

  public async Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default)
  {
    string? titleTemplate = null, messageTemplate = null, priority = "default", tags = null;
    if (ctx.ConfigJson is not null)
    {
      using var cfg = JsonDocument.Parse(ctx.ConfigJson);
      var root = cfg.RootElement;
      titleTemplate = Str(root, "titleTemplate");
      messageTemplate = Str(root, "messageTemplate");
      priority = Str(root, "priority") ?? "default";
      tags = Str(root, "tags");
    }

    var title = TemplateRenderer.Render(titleTemplate, ctx.Entity.Data, ctx.Params);
    var message = TemplateRenderer.Render(messageTemplate, ctx.Entity.Data, ctx.Params);
    if (string.IsNullOrEmpty(message))
    {
      message = title;
    }

    await notifier.SendAsync(message, string.IsNullOrEmpty(title) ? null : title, priority, tags, ct);
    return new HandlerResult("sent");
  }

  private static string? Str(JsonElement e, string name)
  {
    return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
  }

  public ValidationResult ValidateConfig(string? configJson)
  {
    const string Requirement = "notify config requires 'titleTemplate' and/or 'messageTemplate' as a non-empty string — without one, every fire sends an empty notification.";
    using var cfg = ConfigValidation.RequireObject(configJson, Requirement, out var failure);
    if (cfg is null)
    {
      return failure!;
    }

    var errors = new List<string>();
    if (string.IsNullOrEmpty(Str(cfg.RootElement, "titleTemplate")) && string.IsNullOrEmpty(Str(cfg.RootElement, "messageTemplate")))
    {
      errors.Add(Requirement);
    }

    foreach (var name in (string[])["titleTemplate", "messageTemplate", "priority", "tags"])
    {
      if (cfg.RootElement.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.String)
      {
        errors.Add($"notify config '{name}' must be a string.");
      }
    }

    return errors.Count == 0 ? ValidationResult.Valid() : ValidationResult.Invalid(errors);
  }
}
