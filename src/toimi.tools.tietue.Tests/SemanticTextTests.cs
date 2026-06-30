using System.Text.Json;
using toimi.tools.tietue.Semantic;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class SemanticTextTests
{
  private static JsonDocument Doc(string json)
  {
    return JsonDocument.Parse(json);
  }

  [Fact]
  public void Extracts_and_joins_named_string_fields()
  {
    var text = SemanticText.Extract(Doc(/*lang=json,strict*/ """{"title":"hi","body":"there"}"""), ["title", "body"]);
    Assert.Equal("hi there", text);
  }

  [Fact]
  public void Skips_missing_fields()
  {
    var text = SemanticText.Extract(Doc(/*lang=json,strict*/ """{"title":"hi"}"""), ["title", "missing"]);
    Assert.Equal("hi", text);
  }

  [Fact]
  public void Renders_non_string_fields_as_raw_json()
  {
    var text = SemanticText.Extract(Doc(/*lang=json,strict*/ """{"count":3,"tags":["a","b"]}"""), ["count", "tags"]);
    Assert.Equal("""3 ["a","b"]""", text);
  }

  [Fact]
  public void Empty_fields_yields_empty_string()
  {
    Assert.Equal("", SemanticText.Extract(Doc(/*lang=json,strict*/ """{"title":"hi"}"""), []));
  }
}
