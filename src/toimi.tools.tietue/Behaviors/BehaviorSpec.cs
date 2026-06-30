using System.Text.Json;

namespace toimi.tools.tietue.Behaviors;

public record SemanticIndexConfig(string[] Fields, string Mode);

public record UniqueNameConfig(string Field);

public record ExpiryConfig(string Field, string? Prompt);

public static class BehaviorSpec
{
  // Returns the SemanticIndex config from a type's Behaviors JSON, or null if absent/malformed.
  public static SemanticIndexConfig? SemanticIndexOf(string? behaviorsJson)
  {
    if (string.IsNullOrWhiteSpace(behaviorsJson))
    {
      return null;
    }

    JsonDocument doc;
    try
    {
      doc = JsonDocument.Parse(behaviorsJson);
    }
    catch (JsonException)
    {
      return null;
    }

    using (doc)
    {
      if (doc.RootElement.ValueKind != JsonValueKind.Array)
      {
        return null;
      }

      foreach (var item in doc.RootElement.EnumerateArray())
      {
        if (!item.TryGetProperty("behavior", out var b) || b.GetString() != "SemanticIndex")
        {
          continue;
        }

        if (!item.TryGetProperty("config", out var config)
          || !config.TryGetProperty("fields", out var fieldsEl)
          || fieldsEl.ValueKind != JsonValueKind.Array)
        {
          continue;
        }

        var fields = fieldsEl.EnumerateArray()
          .Where(f => f.ValueKind == JsonValueKind.String)
          .Select(f => f.GetString()!)
          .ToArray();

        var mode = config.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String
          ? m.GetString()!
          : "whole";

        return new SemanticIndexConfig(fields, mode);
      }
    }

    return null;
  }

  // Returns the UniqueName config from a type's Behaviors JSON, or null if absent/malformed.
  public static UniqueNameConfig? UniqueNameOf(string? behaviorsJson)
  {
    if (string.IsNullOrWhiteSpace(behaviorsJson))
    {
      return null;
    }

    JsonDocument doc;
    try
    {
      doc = JsonDocument.Parse(behaviorsJson);
    }
    catch (JsonException)
    {
      return null;
    }

    using (doc)
    {
      if (doc.RootElement.ValueKind != JsonValueKind.Array)
      {
        return null;
      }

      foreach (var item in doc.RootElement.EnumerateArray())
      {
        if (!item.TryGetProperty("behavior", out var b) || b.GetString() != "UniqueName")
        {
          continue;
        }

        var field = item.TryGetProperty("config", out var config)
          && config.TryGetProperty("field", out var f)
          && f.ValueKind == JsonValueKind.String
            ? f.GetString()!
            : "name";

        return new UniqueNameConfig(field);
      }
    }

    return null;
  }

  // Returns the Expiry config from a type's Behaviors JSON, or null if absent/malformed.
  public static ExpiryConfig? ExpiryOf(string? behaviorsJson)
  {
    if (string.IsNullOrWhiteSpace(behaviorsJson))
    {
      return null;
    }

    JsonDocument doc;
    try
    {
      doc = JsonDocument.Parse(behaviorsJson);
    }
    catch (JsonException)
    {
      return null;
    }

    using (doc)
    {
      if (doc.RootElement.ValueKind != JsonValueKind.Array)
      {
        return null;
      }

      foreach (var item in doc.RootElement.EnumerateArray())
      {
        if (!item.TryGetProperty("behavior", out var b) || b.GetString() != "Expiry")
        {
          continue;
        }

        var hasConfig = item.TryGetProperty("config", out var config);
        var field = hasConfig && config.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String
          ? f.GetString()!
          : "expiresAt";
        var prompt = hasConfig && config.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String
          ? p.GetString()
          : null;

        return new ExpiryConfig(field, prompt);
      }
    }

    return null;
  }
}
