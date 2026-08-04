using System.Text.Json;

namespace toimi.tools.tietue.Scripts;

public record SetFieldEffect(string Path, string ValueJson);
public record McpCallEffect(string Tool, string ArgsJson);

/// <summary>
/// The declarative result of a script run: the script computes, the host acts.
/// Vocabulary (spec §5.2): setField (entity field writes, applied in-process
/// with schema re-validation) and mcpCall (everything else — notifications,
/// display pushes, triggers — via granted MCP tools).
/// </summary>
public record ScriptEffects(IReadOnlyList<SetFieldEffect> SetFields, IReadOnlyList<McpCallEffect> McpCalls)
{
  public static readonly ScriptEffects Empty = new([], []);

  public static ScriptEffects Parse(string effectsJson)
  {
    try
    {
      using var doc = JsonDocument.Parse(effectsJson);
      var root = doc.RootElement;
      return root.ValueKind != JsonValueKind.Object
        ? Empty
        : new ScriptEffects(ParseSetFields(root), ParseMcpCalls(root));
    }
    catch (JsonException)
    {
      return Empty;
    }
  }

  private static List<SetFieldEffect> ParseSetFields(JsonElement root)
  {
    var result = new List<SetFieldEffect>();
    if (!root.TryGetProperty("setField", out var arr) || arr.ValueKind != JsonValueKind.Array)
    {
      return result;
    }

    foreach (var item in arr.EnumerateArray())
    {
      if (item.ValueKind != JsonValueKind.Object)
      {
        continue;
      }

      var path = Str(item, "path");
      if (path is not null && item.TryGetProperty("value", out var v))
      {
        result.Add(new SetFieldEffect(path, v.GetRawText()));
      }
    }

    return result;
  }

  private static List<McpCallEffect> ParseMcpCalls(JsonElement root)
  {
    var result = new List<McpCallEffect>();
    if (!root.TryGetProperty("mcpCall", out var arr) || arr.ValueKind != JsonValueKind.Array)
    {
      return result;
    }

    foreach (var item in arr.EnumerateArray())
    {
      if (item.ValueKind != JsonValueKind.Object)
      {
        continue;
      }

      var tool = Str(item, "tool");
      if (tool is not null)
      {
        result.Add(new McpCallEffect(tool, item.TryGetProperty("args", out var a) ? a.GetRawText() : "{}"));
      }
    }

    return result;
  }

  private static string? Str(JsonElement e, string name)
  {
    return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
  }
}
