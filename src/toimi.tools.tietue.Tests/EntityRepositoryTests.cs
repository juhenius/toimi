using System.Text.Json.Nodes;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Provisioning;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class EntityRepositoryTests
{
  private const string Schema = /*lang=json,strict*/ """
  {"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}
  """;

  private static async Task<(Data.TietueDbContext db, EntityRepository repo)> SetupAsync()
  {
    var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("note", Schema);
    return (db, new EntityRepository(db, new SchemaValidator()));
  }

  [Fact]
  public async Task Create_valid_entity_persists()
  {
    var (db, repo) = await SetupAsync();
    using var _ = db;

    var e = await repo.CreateAsync("note", JsonNode.Parse("""{"title":"hi"}"""), ["x"]);

    Assert.NotEqual(Guid.Empty, e.Id);
    Assert.Equal("note", e.Type);
    Assert.Equal("hi", e.Data.RootElement.GetProperty("title").GetString());
    Assert.Equal(["x"], e.Tags);
  }

  [Fact]
  public async Task Create_with_unknown_type_throws()
  {
    var (db, repo) = await SetupAsync();
    using var _ = db;
    var ex = await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.CreateAsync("ghost", JsonNode.Parse("""{"title":"hi"}"""), []));
    Assert.Contains(ex.Errors, m => m.Contains("ghost"));
  }

  [Fact]
  public async Task Create_invalid_data_throws_with_errors()
  {
    var (db, repo) = await SetupAsync();
    using var _ = db;
    await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.CreateAsync("note", JsonNode.Parse("""{"count":3}"""), []));
  }

  [Fact]
  public async Task Update_revalidates_and_persists()
  {
    var (db, repo) = await SetupAsync();
    using var _ = db;
    var e = await repo.CreateAsync("note", JsonNode.Parse("""{"title":"hi"}"""), []);

    var updated = await repo.UpdateAsync(e.Id, JsonNode.Parse("""{"title":"bye"}"""), ["t"]);

    Assert.Equal("bye", updated!.Data.RootElement.GetProperty("title").GetString());
    Assert.Equal(["t"], updated.Tags);
    await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.UpdateAsync(e.Id, JsonNode.Parse("""{"count":1}"""), null));
  }

  [Fact]
  public async Task List_filters_by_type_and_tag()
  {
    var (db, repo) = await SetupAsync();
    using var _ = db;
    await repo.CreateAsync("note", JsonNode.Parse("""{"title":"a"}"""), ["keep"]);
    await repo.CreateAsync("note", JsonNode.Parse("""{"title":"b"}"""), ["drop"]);

    var keep = await repo.ListAsync("note", tag: "keep", page: 1, size: 20);

    Assert.Single(keep.Items);
    Assert.Equal(2, (await repo.ListAsync("note", tag: null, page: 1, size: 20)).Total);
  }

  [Fact]
  public async Task Create_drops_blank_tags()
  {
    var (db, repo) = await SetupAsync();
    using var _ = db;

    var e = await repo.CreateAsync("note", JsonNode.Parse("""{"title":"a"}"""), ["ok", "  ", ""]);

    Assert.Equal(["ok"], e.Tags);
  }

  [Fact]
  public async Task Delete_removes_entity()
  {
    var (db, repo) = await SetupAsync();
    using var _ = db;
    var e = await repo.CreateAsync("note", JsonNode.Parse("""{"title":"a"}"""), []);

    Assert.True(await repo.DeleteAsync(e.Id));
    Assert.Null(await repo.GetAsync(e.Id));
  }

  [Fact]
  public async Task Create_provisions_triggers_from_default_triggers()
  {
    var db = TestDb.New();
    using var _ = db;
    const string defaultTriggers = /*lang=json,strict*/ """[{"when":{"atField":"dueAt"},"handler":{"kind":"notify"}}]""";
    await new TypeRepository(db).DefineAsync("event", /*lang=json,strict*/ """{"type":"object","properties":{"title":{"type":"string"},"dueAt":{"type":"string"}}}""", defaultTriggersJson: defaultTriggers);
    var repo = new EntityRepository(db, new SchemaValidator(), [new TriggerProvisioningBehavior(new TriggerProvisioner(new TriggerRepository(db, TestConfig.Default)))]);

    var e = await repo.CreateAsync("event", JsonNode.Parse("""{"title":"Meeting","dueAt":"2026-06-20T09:00:00Z"}"""), []);

    var triggers = await new TriggerRepository(db, TestConfig.Default).ListByEntityAsync(e.Id);
    var t = Assert.Single(triggers);
    Assert.Equal("notify", t.HandlerKind);
  }
}
