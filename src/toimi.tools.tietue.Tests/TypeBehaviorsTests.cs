using toimi.tools.tietue.Behaviors;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TypeBehaviorsTests
{
  [Fact]
  public void Parses_semantic_index_fields_and_mode()
  {
    var b = TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"SemanticIndex","config":{"fields":["content"],"mode":"whole"}}]""");
    Assert.NotNull(b.SemanticIndex);
    Assert.Equal(["content"], b.SemanticIndex.Fields);
    Assert.Equal("whole", b.SemanticIndex.Mode);
  }

  [Fact]
  public void Defaults_semantic_mode_to_whole_when_absent()
  {
    var b = TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"SemanticIndex","config":{"fields":["a","b"]}}]""");
    Assert.Equal("whole", b.SemanticIndex!.Mode);
    Assert.Equal(["a", "b"], b.SemanticIndex.Fields);
  }

  [Fact]
  public void Semantic_index_absent_when_missing_or_unmatched()
  {
    Assert.Null(TypeBehaviors.Parse(null).SemanticIndex);
    Assert.Null(TypeBehaviors.Parse("[]").SemanticIndex);
    Assert.Null(TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"Other","config":{}}]""").SemanticIndex);
  }

  [Fact]
  public void Semantic_index_without_fields_is_skipped_but_a_later_valid_item_wins()
  {
    var b = TypeBehaviors.Parse(/*lang=json,strict*/
      """[{"behavior":"SemanticIndex","config":{}},{"behavior":"SemanticIndex","config":{"fields":["late"]}}]""");
    Assert.Equal(["late"], b.SemanticIndex!.Fields);
  }

  [Fact]
  public void Malformed_json_yields_none()
  {
    Assert.Same(TypeBehaviors.None, TypeBehaviors.Parse("{ not json"));
    Assert.Same(TypeBehaviors.None, TypeBehaviors.Parse(/*lang=json,strict*/ """{"behavior":"SemanticIndex"}"""));
  }

  [Fact]
  public void Parses_unique_name_field()
  {
    var b = TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"UniqueName","config":{"field":"url"}}]""");
    Assert.Equal("url", b.UniqueName!.Field);
  }

  [Fact]
  public void Unique_name_defaults_field_to_name()
  {
    var b = TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"UniqueName"}]""");
    Assert.Equal("name", b.UniqueName!.Field);
  }

  [Fact]
  public void Unique_name_absent_when_missing_or_unmatched()
  {
    Assert.Null(TypeBehaviors.Parse(null).UniqueName);
    Assert.Null(TypeBehaviors.Parse("[]").UniqueName);
    Assert.Null(TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"SemanticIndex","config":{"fields":["a"]}}]""").UniqueName);
  }

  [Fact]
  public void Parses_expiry_field_and_prompt()
  {
    var b = TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"Expiry","config":{"field":"eol","prompt":"check first"}}]""");
    Assert.Equal("eol", b.Expiry!.Field);
    Assert.Equal("check first", b.Expiry.Prompt);
  }

  [Fact]
  public void Expiry_defaults_field_and_leaves_prompt_null()
  {
    var b = TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"Expiry"}]""");
    Assert.Equal("expiresAt", b.Expiry!.Field);
    Assert.Null(b.Expiry.Prompt);
  }

  [Fact]
  public void Parses_all_three_from_one_document()
  {
    var b = TypeBehaviors.Parse(/*lang=json,strict*/
      """[{"behavior":"SemanticIndex","config":{"fields":["content"]}},{"behavior":"UniqueName","config":{"field":"name"}},{"behavior":"Expiry","config":{"field":"expiresAt"}}]""");
    Assert.NotNull(b.SemanticIndex);
    Assert.NotNull(b.UniqueName);
    Assert.NotNull(b.Expiry);
  }

  [Fact]
  public void First_parseable_item_wins_per_kind()
  {
    var b = TypeBehaviors.Parse(/*lang=json,strict*/
      """[{"behavior":"UniqueName","config":{"field":"first"}},{"behavior":"UniqueName","config":{"field":"second"}}]""");
    Assert.Equal("first", b.UniqueName!.Field);
  }
}
