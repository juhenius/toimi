using System.Text.Json;

namespace toimi.tools.tietue.Semantic;

public static class SemanticText
{
  // Concatenates the named fields of an entity's Data into one string for embedding.
  // String fields contribute their value; non-string fields contribute their raw JSON.
  public static string Extract(JsonDocument data, string[] fields)
  {
    var parts = new List<string>();
    foreach (var field in fields)
    {
      if (!data.RootElement.TryGetProperty(field, out var value))
      {
        continue;
      }

      parts.Add(value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? ""
        : value.GetRawText());
    }

    return string.Join(' ', parts);
  }
}
