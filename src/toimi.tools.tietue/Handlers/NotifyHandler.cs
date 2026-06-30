using System.Text.Json;
using toimi.tools.tietue.Notifications;

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

    var title = TemplateRenderer.Render(titleTemplate, ctx.Entity.Data);
    var message = TemplateRenderer.Render(messageTemplate, ctx.Entity.Data);
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
}
