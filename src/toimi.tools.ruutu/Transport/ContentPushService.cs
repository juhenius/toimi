using System.Text.Json;
using toimi.tools.ruutu.Data.Repositories;
using toimi.tools.ruutu.Rendering;

namespace toimi.tools.ruutu.Transport;

public class ContentPushService(
  DisplayRepository displays,
  TemplateRepository templates,
  DisplayEventRepository events,
  Data.RuutuDbContext db,
  SseHub hub,
  ILogger<ContentPushService> logger)
{
  private readonly DbTemplateSource _source = new(templates);

  public async Task ShowSceneAsync(string identifier, string template, JsonElement data, string? actionsJson = null, CancellationToken ct = default)
  {
    if (actionsJson is not null)
    {
      SceneActions.Validate(actionsJson);
    }

    var display = await displays.GetAsync(identifier, ct)
      ?? throw new InvalidOperationException($"Display '{identifier}' not registered");

    var tier = display.Tier ?? "legacy";
    var html = await ScribanRenderer.RenderAsync(template, data, tier, _source, ct);

    display.CurrentTemplate = template;
    display.CurrentData = data.GetRawText();
    display.CurrentActions = actionsJson;
    display.CurrentPushedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);

    await hub.PublishAsync(identifier,
      new SseEvent("scene", JsonSerializer.Serialize(new { html })), ct);
  }

  public async Task ShowOverlayAsync(string identifier, string template, JsonElement data, CancellationToken ct = default)
  {
    var display = await displays.GetAsync(identifier, ct)
      ?? throw new InvalidOperationException($"Display '{identifier}' not registered");

    var tier = display.Tier ?? "legacy";
    var html = await ScribanRenderer.RenderAsync(template, data, tier, _source, ct);

    var stack = OverlayStack.Parse(display.OverlayStack);
    var (next, evicted) = OverlayStack.Push(stack,
      new OverlayFrame(template, data.GetRawText(), DateTimeOffset.UtcNow));
    display.OverlayStack = OverlayStack.Serialize(next);

    if (evicted is not null)
    {
      var droppedPayload = JsonSerializer.Serialize(new
      {
        evicted.Template,
        data = JsonDocument.Parse(evicted.DataJson).RootElement
      });
      await events.AppendAsync(display.Id, "overlay_dropped", null, droppedPayload, ct);
    }
    await db.SaveChangesAsync(ct);

    await hub.PublishAsync(identifier,
      new SseEvent("overlay", JsonSerializer.Serialize(new { html })), ct);
  }

  public async Task DismissTopOverlayAsync(string identifier, CancellationToken ct = default)
  {
    var display = await displays.GetAsync(identifier, ct);
    if (display is null)
    {
      return;
    }

    var stack = OverlayStack.Parse(display.OverlayStack);
    var (next, newTop) = OverlayStack.Pop(stack);
    display.OverlayStack = OverlayStack.Serialize(next);
    await db.SaveChangesAsync(ct);

    if (newTop is null)
    {
      await hub.PublishAsync(identifier, new SseEvent("overlay_clear", "{}"), ct);
    }
    else
    {
      var tier = display.Tier ?? "legacy";
      try
      {
        var html = await ScribanRenderer.RenderAsync(newTop.Template,
          JsonDocument.Parse(newTop.DataJson).RootElement, tier, _source, ct);
        await hub.PublishAsync(identifier,
          new SseEvent("overlay", JsonSerializer.Serialize(new { html })), ct);
      }
#pragma warning disable CA1031 // Graceful degradation: if replacement overlay fails to render, clear instead of crashing
      catch (Exception ex)
#pragma warning restore CA1031
      {
        logger.LogWarning(ex, "Failed to render replacement overlay for '{Identifier}'", identifier);
        await hub.PublishAsync(identifier, new SseEvent("overlay_clear", "{}"), ct);
      }
    }
  }

  public async Task ClearAsync(string identifier, CancellationToken ct = default)
  {
    var display = await displays.GetAsync(identifier, ct);
    if (display is null)
    {
      return;
    }

    display.CurrentTemplate = display.IdleTemplate;
    display.CurrentData = display.IdleData;
    display.CurrentActions = null;
    display.CurrentPushedAt = DateTimeOffset.UtcNow;
    display.OverlayStack = "[]";
    await db.SaveChangesAsync(ct);

    await hub.PublishAsync(identifier, new SseEvent("clear", "{}"), ct);
    await ReplayCurrentStateAsync(identifier, ct);
  }

  public async Task ReplayCurrentStateAsync(string identifier, CancellationToken ct = default)
  {
    var display = await displays.GetAsync(identifier, ct);
    if (display is null)
    {
      return;
    }

    var tier = display.Tier ?? "legacy";
    var (template, dataJson) = (display.CurrentTemplate, display.CurrentData);
    if (template is null)
    {
      (template, dataJson) = (display.IdleTemplate, display.IdleData);
    }

    if (template is not null)
    {
      try
      {
        var data = dataJson is null ? JsonDocument.Parse("{}").RootElement
          : JsonDocument.Parse(dataJson).RootElement;
        var html = await ScribanRenderer.RenderAsync(template, data, tier, _source, ct);
        await hub.PublishAsync(identifier,
          new SseEvent("scene", JsonSerializer.Serialize(new { html })), ct);
      }
#pragma warning disable CA1031 // Graceful degradation: log and continue if replay fails
      catch (Exception ex)
#pragma warning restore CA1031
      {
        logger.LogWarning(ex, "Failed to replay scene for '{Identifier}'", identifier);
      }
    }
    else
    {
      var splashData = JsonDocument.Parse($$"""{ "message": "{{identifier}}" }""").RootElement;
      var html = await ScribanRenderer.RenderAsync("splash", splashData, tier, _source, ct);
      await hub.PublishAsync(identifier,
        new SseEvent("scene", JsonSerializer.Serialize(new { html })), ct);
    }

    var stack = OverlayStack.Parse(display.OverlayStack);
    if (stack.Length > 0)
    {
      try
      {
        var top = stack[0];
        var html = await ScribanRenderer.RenderAsync(top.Template,
          JsonDocument.Parse(top.DataJson).RootElement, tier, _source, ct);
        await hub.PublishAsync(identifier,
          new SseEvent("overlay", JsonSerializer.Serialize(new { html })), ct);
      }
#pragma warning disable CA1031 // Graceful degradation: log and continue if overlay replay fails
      catch (Exception ex)
#pragma warning restore CA1031
      {
        logger.LogWarning(ex, "Failed to replay overlay for '{Identifier}'", identifier);
      }
    }
  }
}
