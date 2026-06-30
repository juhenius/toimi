using System.Text.Json;
using toimi.tools.tietue.Tools;
using toimi.tools.tietue.Types;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TypeToolsTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"title":{"type":"string"}}}""";

  [Fact]
  public async Task DefineType_then_ListTypes_includes_it()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    var define = await new DefineTypeTool(repo).DefineType("note", Schema);
    Assert.Contains("note", define);

    var list = await new ListTypesTool(repo).ListTypes();
    using var doc = JsonDocument.Parse(list);
    Assert.Equal("note", doc.RootElement[0].GetProperty("name").GetString());
    // schema is included for catalog injection
    Assert.True(doc.RootElement[0].TryGetProperty("schema", out _));
  }

  [Fact]
  public async Task DefineType_rejects_bad_schema_with_message()
  {
    using var db = TestDb.New();
    var result = await new DefineTypeTool(new TypeRepository(db)).DefineType("note", "{ not json");
    Assert.Contains("Invalid schema", result);
  }

  [Fact]
  public async Task GetType_and_DeleteType_work()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);
    await new DefineTypeTool(repo).DefineType("note", Schema);

    Assert.Contains("title", await new GetTypeTool(repo).GetType("note"));
    Assert.Contains("deleted", await new DeleteTypeTool(repo).DeleteType("note"));
    Assert.Contains("not found", await new GetTypeTool(repo).GetType("note"));
  }
}
