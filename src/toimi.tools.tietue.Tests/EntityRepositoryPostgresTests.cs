using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Provisioning;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

// Per-test container lifecycle, deliberately (see PostgresTickLockTests): a skipped
// [DockerFact] never constructs the class, so on a docker-less machine no container
// start is ever attempted. Do NOT "optimize" this into an IClassFixture.
public class EntityRepositoryPostgresTests : IAsyncLifetime
{
  private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
    .Build();

  public async Task InitializeAsync()
  {
    await _postgres.StartAsync();
    using var db = NewContext();
    await db.Database.MigrateAsync();
  }

  public Task DisposeAsync()
  {
    return _postgres.DisposeAsync().AsTask();
  }

  // Snake-case naming matches prod (Program.cs) and the checked-in migrations.
  private TietueDbContext NewContext()
  {
    return new TietueDbContext(new DbContextOptionsBuilder<TietueDbContext>()
      .UseNpgsql(_postgres.GetConnectionString())
      .UseSnakeCaseNamingConvention()
      .Options);
  }

  private const string Schema = /*lang=json,strict*/
    """{"type":"object","properties":{"name":{"type":"string"},"content":{"type":"string"},"dueAt":{"type":"string"}},"required":["name"]}""";
  private const string Behaviors = /*lang=json,strict*/
    """[{"behavior":"SemanticIndex","config":{"fields":["content"]}},{"behavior":"UniqueName","config":{"field":"name"}}]""";
  private const string DefaultTriggers = /*lang=json,strict*/
    """[{"when":{"atField":"dueAt"},"handler":{"kind":"notify"}}]""";

  private sealed class ThrowingOnSavedBehavior : IEntityBehavior
  {
    public Task OnSavedAsync(BehaviorContext ctx, CancellationToken ct)
    {
      // Stands at the exact pipeline position TriggerProvisioningBehavior occupies:
      // a provisioning failure after the entity save, inside the ambient transaction.
      throw new InvalidOperationException("simulated provisioning failure");
    }
  }

  [DockerFact]
  public async Task Failed_provisioning_stage_rolls_back_entity_unique_key_and_outbox()
  {
    using (var db = NewContext())
    {
      await new TypeRepository(db).DefineAsync("thing", Schema, Behaviors);
      var idx = new FakeSemanticIndex();
      var repo = new EntityRepository(db, new SchemaValidator(),
        [new SemanticIndexBehavior(new Semantic.SemanticOutbox(db, idx)), new ThrowingOnSavedBehavior()]);

      await Assert.ThrowsAsync<InvalidOperationException>(() =>
        repo.CreateAsync("thing", JsonNode.Parse("""{"name":"a","content":"x"}"""), []));
    }

    using var fresh = NewContext();
    Assert.Empty(await fresh.Entities.ToListAsync());   // the atomicity the comment promises
    Assert.Empty(await fresh.UniqueKeys.ToListAsync());
    Assert.Empty(await fresh.IndexOutbox.ToListAsync());
  }

  [DockerFact]
  public async Task Create_commits_entity_unique_key_trigger_and_drained_outbox_together()
  {
    Guid id;
    var idx = new FakeSemanticIndex();
    using (var db = NewContext())
    {
      await new TypeRepository(db).DefineAsync("thing", Schema, Behaviors, DefaultTriggers);
      var triggers = new TriggerRepository(db, TestConfig.Default);
      var repo = new EntityRepository(db, new SchemaValidator(),
      [
        new SemanticIndexBehavior(new Semantic.SemanticOutbox(db, idx)),
        new TriggerProvisioningBehavior(new TriggerProvisioner(triggers)),
      ]);

      var e = await repo.CreateAsync("thing",
        JsonNode.Parse("""{"name":"a","content":"hello","dueAt":"2026-09-01T09:00:00Z"}"""), []);
      id = e.Id;
    }

    using var fresh = NewContext();
    Assert.NotNull(await fresh.Entities.SingleOrDefaultAsync(e => e.Id == id));
    Assert.Single(await fresh.UniqueKeys.Where(k => k.EntityId == id).ToListAsync());
    Assert.Equal("notify", (await fresh.Triggers.SingleAsync(t => t.EntityId == id)).HandlerKind);
    Assert.Empty(await fresh.IndexOutbox.ToListAsync()); // drained after commit
    Assert.Equal("hello", idx.Store["thing"][id]);
  }
}
