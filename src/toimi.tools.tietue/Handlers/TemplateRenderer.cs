using System.Text.Json;
using System.Text.RegularExpressions;

namespace toimi.tools.tietue.Handlers;

public static partial class TemplateRenderer
{
  [GeneratedRegex(@"\{(\w+)\}")]
  private static partial Regex TokenRegex();

  public static string Render(string? template, JsonDocument data)
  {
    return string.IsNullOrEmpty(template)
      ? ""
      : TokenRegex().Replace(template, m =>
      {
        var key = m.Groups[1].Value;
        return data.RootElement.TryGetProperty(key, out var v)
          ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText())
          : "";
      });
  }
}
