using System.Text.Json;
using System.Text.Json.Nodes;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Semantic;
using toimi.tools.tietue.Tools;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class SearchToolTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"content":{"type":"string"}},"required":["content"]}""";
  private const string Behaviors = /*lang=json,strict*/ """[{"behavior":"SemanticIndex","config":{"fields":["content"]}}]""";

  [Fact]
  public async Task Search_returns_matching_entities()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("note", Schema, Behaviors);
    var idx = new FakeSemanticIndex();
    var dispatcher = new BehaviorDispatcher(db, idx);
    var repo = new EntityRepository(db, new SchemaValidator(), [new SemanticIndexBehavior(new SemanticOutbox(db, idx))]);
    await repo.CreateAsync("note", JsonNode.Parse("""{"content":"apple pie"}"""), []);
    await repo.CreateAsync("note", JsonNode.Parse("""{"content":"zebra"}"""), []);

    var json = await new SearchEntitiesTool(dispatcher).Search("note", "apple", 10);

    using var doc = JsonDocument.Parse(json);
    var items = doc.RootElement.GetProperty("results");
    Assert.Equal(1, items.GetArrayLength());
    Assert.Contains("apple", items[0].GetProperty("data").GetProperty("content").GetString());
  }

  [Fact]
  public async Task Search_unindexed_type_returns_message()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("plain", Schema);
    var dispatcher = new BehaviorDispatcher(db, new FakeSemanticIndex());

    var result = await new SearchEntitiesTool(dispatcher).Search("plain", "x", 10);

    Assert.Contains("not semantically indexed", result);
  }
}
