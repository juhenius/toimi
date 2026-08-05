using System.Text.Json;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Handlers;

/// <summary>Shared write-time parsing for ValidateConfig implementations.</summary>
internal static class ConfigValidation
{
  /// <summary>
  /// Parses a required JSON-object config. Returns null with <paramref name="failure"/> set
  /// when the config is absent (reported as <paramref name="requirement"/>), malformed JSON,
  /// or not an object. The caller owns disposing a non-null result.
  /// </summary>
  public static JsonDocument? RequireObject(string? configJson, string requirement, out ValidationResult? failure)
  {
    if (configJson is null)
    {
      failure = ValidationResult.Invalid(requirement);
      return null;
    }

    JsonDocument doc;
    try
    {
      doc = JsonDocument.Parse(configJson);
    }
    catch (JsonException ex)
    {
      failure = ValidationResult.Invalid($"Config is not valid JSON: {ex.Message}");
      return null;
    }

    if (doc.RootElement.ValueKind != JsonValueKind.Object)
    {
      doc.Dispose();
      failure = ValidationResult.Invalid("Config must be a JSON object.");
      return null;
    }

    failure = null;
    return doc;
  }
}
