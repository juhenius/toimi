using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace toimi.tools.tietue.Validation;

public class SchemaValidator
{
  private static readonly EvaluationOptions Options = new()
  {
    OutputFormat = OutputFormat.List,
  };

  public ValidationResult Validate(string schemaJson, JsonNode? data)
  {
    JsonSchema schema;
    try
    {
      schema = JsonSchema.FromText(schemaJson);
    }
    catch (Exception ex)
    {
      return ValidationResult.Invalid($"Invalid schema: {ex.Message}");
    }

    var results = schema.Evaluate(JsonSerializer.SerializeToElement(data), Options);
    if (results.IsValid)
    {
      return ValidationResult.Valid();
    }

    var errors = results.Details
      .Where(d => d.Errors is { Count: > 0 })
      .SelectMany(d => d.Errors!.Select(e =>
        string.IsNullOrEmpty(d.InstanceLocation.ToString())
          ? e.Value
          : $"{d.InstanceLocation}: {e.Value}"))
      .Distinct()
      .ToList();

    if (errors.Count == 0)
    {
      errors.Add("Data does not match the type schema.");
    }

    return ValidationResult.Invalid(errors);
  }
}
