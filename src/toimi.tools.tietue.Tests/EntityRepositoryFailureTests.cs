using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Provisioning;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

// Handlers run inside the scheduler tick on a scoped DbContext shared with the trigger
// batch: a repository call that throws must leave NOTHING pending, or the tick's later
// finalize/advance SaveChangesAsync silently commits the half-applied mutation.
public class EntityRepositoryFailureTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"name":{"type":"string"},"note":{"type":"string"}},"required":["name"]}""";
  private const string Behaviors = /*lang=json,strict*/ """[{"behavior":"UniqueName","config":{"field":"name"}}]""";

  private static async Task<(TietueDbContext db, EntityRepository repo)> SetupAsync()
  {
    var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("memory", Schema, Behaviors);
    var repo = new EntityRepository(db, new SchemaValidator());
    return (db, repo);
  }

  [Fact]
  public async Task Failed_update_unique_precheck_leaves_no_pending_changes()
  {
    var (db, repo) = await SetupAsync();
    using var _ = db;
    await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"a"}"""), []);
    var b = await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"b","note":"original"}"""), []);

    await Assert.ThrowsAsync<TietueValidationException>(() =>
      repo.UpdateAsync(b.Id, JsonNode.Parse("""{"name":"a","note":"poisoned"}"""), null));

    Assert.False(db.ChangeTracker.HasChanges());

    // Simulate the scheduler tick's later save flushing whatever is tracked.
    await db.SaveChangesAsync();
    using var fresh = TestDb.SameStore(db);
    var reloaded = await fresh.Entities.SingleAsync(e => e.Id == b.Id);
    Assert.Contains("original", reloaded.Data.RootElement.GetRawText());
    Assert.DoesNotContain("poisoned", reloaded.Data.RootElement.GetRawText());
  }

  [Fact]
  public async Task Failed_create_unique_precheck_leaves_no_pending_changes()
  {
    var (db, repo) = await SetupAsync();
    using var _ = db;
    await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"a"}"""), []);

    await Assert.ThrowsAsync<TietueValidationException>(() =>
      repo.CreateAsync("memory", JsonNode.Parse("""{"name":"a"}"""), []));

    Assert.False(db.ChangeTracker.HasChanges());
    Assert.Equal(1, await db.Entities.CountAsync());
    Assert.Empty(await db.IndexOutbox.ToListAsync());
  }

  [Fact]
  public async Task Create_propagates_provisioning_failure()
  {
    // A default-trigger type so CreateAsync provisions; the provisioner's TriggerRepository
    // runs over a context whose next save throws, standing in for a mid-provision failure.
    // Under Postgres the surrounding transaction rolls the entity back too; the InMemory
    // provider can't begin a real transaction, so this pins the exception propagation the
    // caller (and scheduler tick) must see — the full ROLLBACK is only exercised on Postgres.
    const string reminderSchema = /*lang=json,strict*/ """{"type":"object","properties":{"dueAt":{"type":"string"}},"required":["dueAt"]}""";
    const string defaultTriggers = /*lang=json,strict*/ """[{"when":{"atField":"dueAt"},"handler":{"kind":"notify"}}]""";

    var db = TestDb.New();
    using var _ = db;
    await new TypeRepository(db).DefineAsync("reminder", reminderSchema, defaultTriggersJson: defaultTriggers);

    var throwDb = TestDb.NewThrowingOnce();
    using var __ = throwDb;
    throwDb.ThrowNext = true;
    var provisioner = new TriggerProvisioner(new TriggerRepository(throwDb, TestConfig.Default));
    var repo = new EntityRepository(db, new SchemaValidator(), [new TriggerProvisioningBehavior(provisioner)]);

    await Assert.ThrowsAnyAsync<Exception>(() =>
      repo.CreateAsync("reminder", JsonNode.Parse("""{"dueAt":"2026-06-01T09:00:00Z"}"""), []));
  }

  [Fact]
  public async Task Unique_index_violation_resets_pending_changes()
  {
    // Drives the SaveGuardingUniqueAsync 23505 catch, unreachable under InMemory
    // (no unique enforcement) without a context that throws on demand.
    var db = TestDb.NewThrowingOnce();
    using var _ = db;
    await new TypeRepository(db).DefineAsync("memory", Schema, Behaviors);
    var repo = new EntityRepository(db, new SchemaValidator());
    var e = await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"a","note":"original"}"""), []);

    db.ThrowNext = true;
    await Assert.ThrowsAsync<TietueValidationException>(() =>
      repo.UpdateAsync(e.Id, JsonNode.Parse("""{"name":"a","note":"poisoned"}"""), null));

    Assert.False(db.ChangeTracker.HasChanges());
    await db.SaveChangesAsync();
    using var fresh = TestDb.SameStore(db);
    var reloaded = await fresh.Entities.SingleAsync(x => x.Id == e.Id);
    Assert.Contains("original", reloaded.Data.RootElement.GetRawText());
  }
}
