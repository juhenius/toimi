using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TypeRepositoryTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"title":{"type":"string"}}}""";

  private static HandlerRegistry NotifyOnly()
  {
    return new HandlerRegistry([new NotifyHandler(new FakeNotifier())]);
  }

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
    await Assert.ThrowsAsync<TietueValidationException>(
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

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"not":"an array"}""", "must be a JSON array")]
  [InlineData(/*lang=json,strict*/ """[{"handler":{"kind":"notify","config":{"titleTemplate":"{t}"}}}]""", "atField")]
  [InlineData(/*lang=json,strict*/ """[{"when":{"atField":""},"handler":{"kind":"notify","config":{"titleTemplate":"{t}"}}}]""", "atField")]
  [InlineData(/*lang=json,strict*/ """[{"when":{"atField":"dueAt"}}]""", "handler.kind")]
  public async Task Define_rejects_structurally_broken_default_triggers(string defaultTriggers, string expectedError)
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db); // structure checks need no registry

    var ex = await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.DefineAsync("broken", /*lang=json,strict*/ """{"type":"object"}""", null, defaultTriggers));

    Assert.Contains(expectedError, ex.Message);
    Assert.Null(await repo.GetAsync("broken"));
  }

  [Fact]
  public async Task Define_with_registry_rejects_unknown_handler_kind()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db, NotifyOnly());

    var ex = await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.DefineAsync("broken", /*lang=json,strict*/ """{"type":"object"}""", null,
        /*lang=json,strict*/ """[{"when":{"atField":"dueAt"},"handler":{"kind":"nootify","config":{"titleTemplate":"{t}"}}}]"""));

    Assert.Contains("nootify", ex.Message);
  }

  [Fact]
  public async Task Define_with_registry_rejects_config_the_handler_cannot_run()
  {
    // Finding 4's provisioner tail: a typo'd template used to be stamped onto every new
    // entity and then silently skipped or uselessly fired. Now define_type refuses it.
    using var db = TestDb.New();
    var repo = new TypeRepository(db, NotifyOnly());

    var ex = await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.DefineAsync("broken", /*lang=json,strict*/ """{"type":"object"}""", null,
        /*lang=json,strict*/ """[{"when":{"atField":"dueAt"},"handler":{"kind":"notify","config":{"titelTemplate":"{t}"}}}]"""));

    Assert.Contains("titleTemplate", ex.Message);
  }

  [Fact]
  public async Task Define_without_registry_accepts_unknown_kind_structure_only()
  {
    // Null registry (bare test construction) = structural checks only, by design.
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    var t = await repo.DefineAsync("loose", /*lang=json,strict*/ """{"type":"object"}""", null,
      /*lang=json,strict*/ """[{"when":{"atField":"dueAt"},"handler":{"kind":"whatever"}}]""");

    Assert.Equal("loose", t.Name);
  }
}
