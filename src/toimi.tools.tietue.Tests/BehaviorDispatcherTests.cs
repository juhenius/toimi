using System.Text.Json;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Types;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class BehaviorDispatcherTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"content":{"type":"string"}}}""";
  private const string Behaviors = /*lang=json,strict*/ """[{"behavior":"SemanticIndex","config":{"fields":["content"]}}]""";

  private static async Task<(TietueDbContext db, FakeSemanticIndex idx, BehaviorDispatcher disp)> SetupAsync(string? behaviors)
  {
    var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("note", Schema, behaviors);
    var idx = new FakeSemanticIndex();
    return (db, idx, new BehaviorDispatcher(db, idx));
  }

  private static Entity NewEntity(string content)
  {
    return new()
    {
      Id = Guid.NewGuid(),
      Type = "note",
      Data = JsonDocument.Parse($$"""{"content":"{{content}}"}"""),
      CreatedAt = DateTimeOffset.UtcNow,
      UpdatedAt = DateTimeOffset.UtcNow,
    };
  }

  [Fact]
  public async Task Search_returns_matching_entities_ordered_by_score()
  {
    var (db, idx, disp) = await SetupAsync(Behaviors);
    using var _ = db;
    var match = NewEntity("apple banana");
    var other = NewEntity("zebra");
    db.Entities.AddRange(match, other);
    await db.SaveChangesAsync();
    await idx.IndexAsync("note", match.Id, "apple banana");
    await idx.IndexAsync("note", other.Id, "zebra");

    var results = await disp.SearchAsync("note", "apple", 10);

    var hit = Assert.Single(results);
    Assert.Equal(match.Id, hit.Entity.Id);
  }

  [Fact]
  public async Task Search_throws_for_type_without_semantic_index()
  {
    var (db, idx, disp) = await SetupAsync(behaviors: null);
    using var _ = db;
    await Assert.ThrowsAsync<Validation.TietueValidationException>(
      () => disp.SearchAsync("note", "x", 10));
  }
}
