using System.Text.Json;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Tools;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class EntityToolsTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""";

  private static async Task<EntityRepository> RepoWithNoteTypeAsync(Data.TietueDbContext db)
  {
    await new TypeRepository(db).DefineAsync("note", Schema);
    return new EntityRepository(db, new SchemaValidator());
  }

  [Fact]
  public async Task Create_get_update_delete_round_trip()
  {
    using var db = TestDb.New();
    var repo = await RepoWithNoteTypeAsync(db);

    var created = await new CreateEntityTool(repo).Create("note", /*lang=json,strict*/ """{"title":"hi"}""", null);
    using var createdDoc = JsonDocument.Parse(created);
    var id = createdDoc.RootElement.GetProperty("id").GetString()!;

    Assert.Contains("hi", await new GetEntityTool(repo).Get(id));
    Assert.Contains("bye", await new UpdateEntityTool(repo).Update(id, /*lang=json,strict*/ """{"title":"bye"}""", null));
    Assert.Contains("deleted", await new DeleteEntityTool(repo).Delete(id));
    Assert.Contains("not found", await new GetEntityTool(repo).Get(id));
  }

  [Fact]
  public async Task Create_invalid_data_returns_validation_message()
  {
    using var db = TestDb.New();
    var repo = await RepoWithNoteTypeAsync(db);
    var result = await new CreateEntityTool(repo).Create("note", /*lang=json,strict*/ """{"count":3}""", null);
    Assert.Contains("title", result, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Create_with_malformed_json_returns_message()
  {
    using var db = TestDb.New();
    var repo = await RepoWithNoteTypeAsync(db);
    var result = await new CreateEntityTool(repo).Create("note", "{ not json", null);
    Assert.Contains("Invalid data JSON", result);
  }

  [Fact]
  public async Task List_returns_entities_of_type()
  {
    using var db = TestDb.New();
    var repo = await RepoWithNoteTypeAsync(db);
    await new CreateEntityTool(repo).Create("note", /*lang=json,strict*/ """{"title":"a"}""", "x,y");

    var list = await new ListEntitiesTool(repo).List("note", null, 1, 20);
    using var doc = JsonDocument.Parse(list);
    Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt32());
  }
}
