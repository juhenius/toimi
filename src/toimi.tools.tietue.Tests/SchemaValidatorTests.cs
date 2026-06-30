using System.Text.Json.Nodes;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class SchemaValidatorTests
{
  private const string Schema = /*lang=json,strict*/ """
  {
    "type": "object",
    "properties": { "title": { "type": "string" }, "count": { "type": "integer" } },
    "required": ["title"]
  }
  """;

  private readonly SchemaValidator _validator = new();

  [Fact]
  public void Valid_data_passes()
  {
    var data = JsonNode.Parse("""{"title":"hi","count":3}""");
    var result = _validator.Validate(Schema, data);
    Assert.True(result.IsValid);
    Assert.Empty(result.Errors);
  }

  [Fact]
  public void Missing_required_field_fails()
  {
    var data = JsonNode.Parse("""{"count":3}""");
    var result = _validator.Validate(Schema, data);
    Assert.False(result.IsValid);
    Assert.NotEmpty(result.Errors);
  }

  [Fact]
  public void Wrong_type_fails()
  {
    var data = JsonNode.Parse("""{"title":"hi","count":"three"}""");
    var result = _validator.Validate(Schema, data);
    Assert.False(result.IsValid);
  }

  [Fact]
  public void Malformed_schema_reports_invalid_schema()
  {
    var result = _validator.Validate("{ not json", JsonNode.Parse("{}"));
    Assert.False(result.IsValid);
    Assert.Contains(result.Errors, e => e.Contains("schema", StringComparison.OrdinalIgnoreCase));
  }
}
