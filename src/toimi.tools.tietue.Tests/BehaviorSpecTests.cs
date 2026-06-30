using toimi.tools.tietue.Behaviors;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class BehaviorSpecTests
{
  [Fact]
  public void Parses_semantic_index_fields_and_mode()
  {
    var cfg = BehaviorSpec.SemanticIndexOf(
                           /*lang=json,strict*/
                           """[{"behavior":"SemanticIndex","config":{"fields":["content"],"mode":"whole"}}]""");
    Assert.NotNull(cfg);
    Assert.Equal(["content"], cfg.Fields);
    Assert.Equal("whole", cfg.Mode);
  }

  [Fact]
  public void Defaults_mode_to_whole_when_absent()
  {
    var cfg = BehaviorSpec.SemanticIndexOf(
                           /*lang=json,strict*/
                           """[{"behavior":"SemanticIndex","config":{"fields":["a","b"]}}]""");
    Assert.Equal("whole", cfg!.Mode);
  }

  [Fact]
  public void Null_when_no_semantic_index_behavior()
  {
    Assert.Null(BehaviorSpec.SemanticIndexOf(null));
    Assert.Null(BehaviorSpec.SemanticIndexOf("[]"));
    Assert.Null(BehaviorSpec.SemanticIndexOf(/*lang=json,strict*/ """[{"behavior":"Other","config":{}}]"""));
  }

  [Fact]
  public void Null_on_malformed_json()
  {
    Assert.Null(BehaviorSpec.SemanticIndexOf("{ not json"));
  }

  [Fact]
  public void Parses_unique_name_field()
  {
    var cfg = BehaviorSpec.UniqueNameOf(
                           /*lang=json,strict*/
                           """[{"behavior":"UniqueName","config":{"field":"url"}}]""");
    Assert.NotNull(cfg);
    Assert.Equal("url", cfg.Field);
  }

  [Fact]
  public void Unique_name_defaults_field_to_name()
  {
    var cfg = BehaviorSpec.UniqueNameOf(/*lang=json,strict*/ """[{"behavior":"UniqueName"}]""");
    Assert.Equal("name", cfg!.Field);
  }

  [Fact]
  public void Null_when_no_unique_name_behavior()
  {
    Assert.Null(BehaviorSpec.UniqueNameOf(null));
    Assert.Null(BehaviorSpec.UniqueNameOf("[]"));
    Assert.Null(BehaviorSpec.UniqueNameOf(/*lang=json,strict*/ """[{"behavior":"SemanticIndex","config":{"fields":["a"]}}]"""));
    Assert.Null(BehaviorSpec.UniqueNameOf("{ not json"));
  }

  [Fact]
  public void Parses_expiry_field_and_prompt()
  {
    var cfg = BehaviorSpec.ExpiryOf(/*lang=json,strict*/ """[{"behavior":"Expiry","config":{"field":"eol","prompt":"check first"}}]""");
    Assert.NotNull(cfg);
    Assert.Equal("eol", cfg.Field);
    Assert.Equal("check first", cfg.Prompt);
  }

  [Fact]
  public void Expiry_defaults_field_and_null_prompt()
  {
    var cfg = BehaviorSpec.ExpiryOf(/*lang=json,strict*/ """[{"behavior":"Expiry"}]""");
    Assert.Equal("expiresAt", cfg!.Field);
    Assert.Null(cfg.Prompt);
  }

  [Fact]
  public void Null_when_no_expiry_behavior()
  {
    Assert.Null(BehaviorSpec.ExpiryOf(null));
    Assert.Null(BehaviorSpec.ExpiryOf("[]"));
    Assert.Null(BehaviorSpec.ExpiryOf(/*lang=json,strict*/ """[{"behavior":"SemanticIndex","config":{"fields":["a"]}}]"""));
    Assert.Null(BehaviorSpec.ExpiryOf("{ not json"));
  }
}
