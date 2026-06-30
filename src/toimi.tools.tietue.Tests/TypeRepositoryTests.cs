using toimi.tools.tietue.Types;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TypeRepositoryTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"title":{"type":"string"}}}""";

  [Fact]
  public async Task Define_then_get_returns_type()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    await repo.DefineAsync("note", Schema);
    var t = await repo.GetAsync("note");

    Assert.NotNull(t);
    Assert.Equal("note", t.Name);
    Assert.Equal("string",
      t.JsonSchema.RootElement.GetProperty("properties").GetProperty("title").GetProperty("type").GetString());
  }

  [Fact]
  public async Task Define_is_upsert_by_name()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    await repo.DefineAsync("note", Schema);
    await repo.DefineAsync("note", /*lang=json,strict*/ """{"type":"object","properties":{"body":{"type":"string"}}}""");

    var t = await repo.GetAsync("note");
    Assert.True(t!.JsonSchema.RootElement.GetProperty("properties").TryGetProperty("body", out _));
    Assert.Single(await repo.ListAsync());
  }

  [Fact]
  public async Task Define_rejects_malformed_schema()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);
    await Assert.ThrowsAsync<Validation.TietueValidationException>(
      () => repo.DefineAsync("note", "{ not json"));
  }

  [Fact]
  public async Task Delete_removes_type()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);
    await repo.DefineAsync("note", Schema);

    var deleted = await repo.DeleteAsync("note");

    Assert.True(deleted);
    Assert.Null(await repo.GetAsync("note"));
  }
}
