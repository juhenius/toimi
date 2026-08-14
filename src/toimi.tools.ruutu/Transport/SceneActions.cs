using System.Text.Json;

namespace toimi.tools.ruutu.Transport;

/// <summary>
/// The scene-scoped actions map: event selector → webhook capability URL
/// (see docs/adr/0002). A selector is "&lt;type&gt;" or "&lt;type&gt;:&lt;target&gt;";
/// the more specific form wins at resolve time. Stored as jsonb on the
/// display row and replaced wholesale with every scene push.
/// </summary>
public static class SceneActions
{
  /// <summary>
  /// Validates an actions map at push time. Throws InvalidOperationException
  /// (ToolGuard-translated) so a bad map rejects the whole push.
  /// </summary>
  public static void Validate(string actionsJson)
  {
    JsonDocument doc;
    try
    {
      doc = JsonDocument.Parse(actionsJson);
    }
    catch (JsonException ex)
    {
      throw new InvalidOperationException($"actionsJson is not valid JSON: {ex.Message}");
    }

    using (doc)
    {
      if (doc.RootElement.ValueKind != JsonValueKind.Object)
      {
        throw new InvalidOperationException("actionsJson must be a JSON object mapping event selectors to webhook URLs.");
      }

      foreach (var prop in doc.RootElement.EnumerateObject())
      {
        if (string.IsNullOrWhiteSpace(prop.Name))
        {
          throw new InvalidOperationException("actionsJson selectors must be non-empty (\"<type>\" or \"<type>:<target>\").");
        }

        if (prop.Value.ValueKind != JsonValueKind.String
          || !Uri.TryCreate(prop.Value.GetString(), UriKind.Absolute, out var uri)
          || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
          throw new InvalidOperationException(
            $"actionsJson value for '{prop.Name}' must be an absolute http(s) webhook URL.");
        }
      }
    }
  }

  /// <summary>
  /// Resolves an incoming event against a stored actions map.
  /// "type:target" beats "type"; null when nothing is wired (or the stored
  /// map is unreadable — resolve never throws, events must keep flowing).
  /// </summary>
  public static string? Resolve(string? actionsJson, string type, string? target)
  {
    if (string.IsNullOrEmpty(actionsJson))
    {
      return null;
    }

    try
    {
      using var doc = JsonDocument.Parse(actionsJson);
      var root = doc.RootElement;
      return root.ValueKind != JsonValueKind.Object
        ? null
        : (target is not null ? UrlFor(root, $"{type}:{target}") : null) ?? UrlFor(root, type);
    }
    catch (JsonException)
    {
      return null;
    }
  }

  private static string? UrlFor(JsonElement root, string selector)
  {
    return root.TryGetProperty(selector, out var value) && value.ValueKind == JsonValueKind.String
      ? value.GetString()
      : null;
  }
}
