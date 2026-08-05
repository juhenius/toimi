using System.Text.Json;

namespace toimi.tools.tietue.Behaviors;

public record SemanticIndexConfig(string[] Fields, string Mode);

public record UniqueNameConfig(string Field);

public record ExpiryConfig(string Field, string? Prompt);

/// <summary>
/// A type's Behaviors JSON parsed once into typed configs. Unknown behaviors are
/// ignored; malformed JSON yields <see cref="None"/>; per kind the first parseable
/// item wins (an item with an unusable config is skipped, so a later valid one applies).
/// </summary>
public sealed record TypeBehaviors(
  SemanticIndexConfig? SemanticIndex,
  UniqueNameConfig? UniqueName,
  ExpiryConfig? Expiry)
{
  public static readonly TypeBehaviors None = new(null, null, null);

  public static TypeBehaviors Parse(string? behaviorsJson)
  {
    if (string.IsNullOrWhiteSpace(behaviorsJson))
    {
      return None;
    }

    JsonDocument doc;
    try
    {
      doc = JsonDocument.Parse(behaviorsJson);
    }
    catch (JsonException)
    {
      return None;
    }

    using (doc)
    {
      if (doc.RootElement.ValueKind != JsonValueKind.Array)
      {
        return None;
      }

      SemanticIndexConfig? semantic = null;
      UniqueNameConfig? unique = null;
      ExpiryConfig? expiry = null;

      foreach (var item in doc.RootElement.EnumerateArray())
      {
        if (!item.TryGetProperty("behavior", out var kind) || kind.ValueKind != JsonValueKind.String)
        {
          continue;
        }

        switch (kind.GetString())
        {
          case "SemanticIndex":
            semantic ??= ParseSemanticIndex(item);
            break;
          case "UniqueName":
            unique ??= ParseUniqueName(item);
            break;
          case "Expiry":
            expiry ??= ParseExpiry(item);
            break;
          default:
            break;
        }
      }

      return semantic is null && unique is null && expiry is null
        ? None
        : new TypeBehaviors(semantic, unique, expiry);
    }
  }

  private static SemanticIndexConfig? ParseSemanticIndex(JsonElement item)
  {
    if (!item.TryGetProperty("config", out var config)
      || !config.TryGetProperty("fields", out var fieldsEl)
      || fieldsEl.ValueKind != JsonValueKind.Array)
    {
      return null;
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

  private static UniqueNameConfig ParseUniqueName(JsonElement item)
  {
    var field = item.TryGetProperty("config", out var config)
      && config.TryGetProperty("field", out var f)
      && f.ValueKind == JsonValueKind.String
        ? f.GetString()!
        : "name";

    return new UniqueNameConfig(field);
  }

  private static ExpiryConfig ParseExpiry(JsonElement item)
  {
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
