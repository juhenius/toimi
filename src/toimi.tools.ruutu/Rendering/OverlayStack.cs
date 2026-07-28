using System.Text.Json;

namespace toimi.tools.ruutu.Rendering;

#pragma warning disable CA1711 // OverlayStack is intentionally named as a stack data structure
public static class OverlayStack
#pragma warning restore CA1711
{
  public const int MaxDepth = 10;

  public static OverlayFrame[] Parse(string json)
  {
    if (string.IsNullOrWhiteSpace(json))
    {
      return [];
    }

    using var doc = JsonDocument.Parse(json);
    if (doc.RootElement.ValueKind != JsonValueKind.Array)
    {
      return [];
    }

    var frames = new List<OverlayFrame>();
    foreach (var el in doc.RootElement.EnumerateArray())
    {
      var template = el.GetProperty("template").GetString() ?? "";
      var data = el.GetProperty("data").GetRawText();
      var enq = el.GetProperty("enqueued_at").GetDateTimeOffset();
      frames.Add(new OverlayFrame(template, data, enq));
    }
    return [.. frames];
  }

  public static string Serialize(IReadOnlyList<OverlayFrame> frames)
  {
    using var ms = new MemoryStream();
    using (var w = new Utf8JsonWriter(ms))
    {
      w.WriteStartArray();
      foreach (var f in frames)
      {
        w.WriteStartObject();
        w.WriteString("template", f.Template);
        w.WritePropertyName("data");
        using var d = JsonDocument.Parse(f.DataJson);
        d.RootElement.WriteTo(w);
        w.WriteString("enqueued_at", f.EnqueuedAt.UtcDateTime.ToString("o"));
        w.WriteEndObject();
      }
      w.WriteEndArray();
    }
    return System.Text.Encoding.UTF8.GetString(ms.ToArray());
  }

  /// <summary>Push onto top of LIFO stack. Returns new stack and evicted frame (or null) if oldest was dropped.</summary>
  public static (OverlayFrame[] Stack, OverlayFrame? Evicted) Push(IReadOnlyList<OverlayFrame> current, OverlayFrame frame)
  {
    var list = new List<OverlayFrame>(current.Count + 1) { frame };
    list.AddRange(current);
    OverlayFrame? evicted = null;
    if (list.Count > MaxDepth)
    {
      evicted = list[^1];
      list.RemoveAt(list.Count - 1);
    }
    return (list.ToArray(), evicted);
  }

  /// <summary>Pop the top of the stack. Returns the remainder and the NEW top (if any).</summary>
  public static (OverlayFrame[] Stack, OverlayFrame? NewTop) Pop(IReadOnlyList<OverlayFrame> current)
  {
    if (current.Count == 0)
    {
      return ([], null);
    }

    var remainder = current.Skip(1).ToArray();
    var newTop = remainder.Length > 0 ? remainder[0] : null;
    return (remainder, newTop);
  }
}
