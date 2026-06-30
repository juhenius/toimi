using System.Text.Json.Nodes;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class EntityRepositoryIndexingTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"content":{"type":"string"}},"required":["content"]}""";
  private const string Behaviors = /*lang=json,strict*/ """[{"behavior":"SemanticIndex","config":{"fields":["content"]}}]""";

  private static async Task<(EntityRepository repo, FakeSemanticIndex idx)> SetupAsync(Data.TietueDbContext db)
  {
    await new TypeRepository(db).DefineAsync("note", Schema, Behaviors);
    var idx = new FakeSemanticIndex();
    var repo = new EntityRepository(db, new SchemaValidator(), new BehaviorDispatcher(db, idx));
    return (repo, idx);
  }

  [Fact]
  public async Task Create_indexes_entity()
  {
    using var db = TestDb.New();
    var (repo, idx) = await SetupAsync(db);

    var e = await repo.CreateAsync("note", JsonNode.Parse("""{"content":"hello"}"""), []);

    Assert.Equal("hello", idx.Store["note"][e.Id]);
  }

  [Fact]
  public async Task Update_reindexes_entity()
  {
    using var db = TestDb.New();
    var (repo, idx) = await SetupAsync(db);
    var e = await repo.CreateAsync("note", JsonNode.Parse("""{"content":"hello"}"""), []);

    await repo.UpdateAsync(e.Id, JsonNode.Parse("""{"content":"goodbye"}"""), null);

    Assert.Equal("goodbye", idx.Store["note"][e.Id]);
  }

  [Fact]
  public async Task Delete_deindexes_entity()
  {
    using var db = TestDb.New();
    var (repo, idx) = await SetupAsync(db);
    var e = await repo.CreateAsync("note", JsonNode.Parse("""{"content":"hello"}"""), []);

    await repo.DeleteAsync(e.Id);

    Assert.False(idx.Store["note"].ContainsKey(e.Id));
  }
}
