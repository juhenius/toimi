using System.Text.Json;

namespace toimi.tools.tietue.Scripts;

public record SetFieldEffect(string Path, string ValueJson);
public record NotifyEffect(string Message, string? Title, string? Priority);
public record TriggerEffect(string ScheduleJson, string HandlerKind, string? HandlerConfigJson);

public record ScriptEffects(
  SetFieldEffect? SetField,
  NotifyEffect? Notify,
  TriggerEffect? Trigger,
  string? Escalate)
{
  public static ScriptEffects Parse(string effectsJson)
  {
    try
    {
      using var doc = JsonDocument.Parse(effectsJson);
      var root = doc.RootElement;
      return root.ValueKind != JsonValueKind.Object
        ? Empty
        : new ScriptEffects(
        ParseSetField(root),
        ParseNotify(root),
        ParseTrigger(root),
        Str(root, "escalate"));
    }
    catch (JsonException)
    {
      return Empty;
    }
  }

  private static readonly ScriptEffects Empty = new(null, null, null, null);

  private static SetFieldEffect? ParseSetField(JsonElement root)
  {
    if (!root.TryGetProperty("setField", out var sf) || sf.ValueKind != JsonValueKind.Object)
    {
      return null;
    }

    var path = Str(sf, "path");
    return path is null || !sf.TryGetProperty("value", out var v) ? null : new SetFieldEffect(path, v.GetRawText());
  }

  private static NotifyEffect? ParseNotify(JsonElement root)
  {
    if (!root.TryGetProperty("notify", out var n) || n.ValueKind != JsonValueKind.Object)
    {
      return null;
    }

    var message = Str(n, "message");
    return message is null ? null : new NotifyEffect(message, Str(n, "title"), Str(n, "priority"));
  }

  private static TriggerEffect? ParseTrigger(JsonElement root)
  {
    if (!root.TryGetProperty("trigger", out var t) || t.ValueKind != JsonValueKind.Object)
    {
      return null;
    }

    var kind = Str(t, "handlerKind");
    return kind is null || !t.TryGetProperty("schedule", out var s)
      ? null
      : new TriggerEffect(s.GetRawText(), kind, t.TryGetProperty("handlerConfig", out var c) ? c.GetRawText() : null);
  }

  private static string? Str(JsonElement e, string name)
  {
    return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
  }
}
