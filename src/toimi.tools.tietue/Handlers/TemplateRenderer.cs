using System.Text.Json;
using System.Text.RegularExpressions;

namespace toimi.tools.tietue.Handlers;

public static partial class TemplateRenderer
{
  [GeneratedRegex(@"\{(\w+)\}")]
  private static partial Regex TokenRegex();

  /// <summary>
  /// {token} resolution order: entity data first, then the firing's params. Data wins
  /// deliberately — params come from whoever holds a webhook's capability URL and must
  /// not shadow the entity's own fields.
  /// </summary>
  public static string Render(string? template, JsonDocument data, JsonElement? @params = null)
  {
    return string.IsNullOrEmpty(template)
      ? ""
      : TokenRegex().Replace(template, m =>
      {
        var key = m.Groups[1].Value;
        return data.RootElement.TryGetProperty(key, out var v)
          ? Text(v)
          : @params is { } p && p.TryGetProperty(key, out var pv) ? Text(pv) : "";
      });
  }

  private static string Text(JsonElement value)
  {
    return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
  }
}
