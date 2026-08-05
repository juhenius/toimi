using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Provisioning;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Semantic;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class BehaviorPipelineTests
{
  private const string Schema = /*lang=json,strict*/
    """{"type":"object","properties":{"name":{"type":"string"},"content":{"type":"string"},"expiresAt":{"type":"string"},"dueAt":{"type":"string"}},"required":["name"]}""";
  private const string AllThreeBehaviors = /*lang=json,strict*/
    """[{"behavior":"SemanticIndex","config":{"fields":["content"]}},{"behavior":"UniqueName","config":{"field":"name"}},{"behavior":"Expiry","config":{"field":"expiresAt"}}]""";
  private const string DefaultTriggers = /*lang=json,strict*/
    """[{"when":{"atField":"dueAt"},"handler":{"kind":"notify"}}]""";

  private static async Task<(Data.TietueDbContext db, EntityRepository repo, FakeSemanticIndex idx)> SetupAsync()
  {
    var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("wish", Schema, AllThreeBehaviors, DefaultTriggers);
    var idx = new FakeSemanticIndex();
    var triggers = new TriggerRepository(db, TestConfig.Default);
    var repo = new EntityRepository(db, new SchemaValidator(),
    [
      new SemanticIndexBehavior(new SemanticOutbox(db, idx)),
      new TriggerProvisioningBehavior(new TriggerProvisioner(triggers)),
      new ExpiryBehavior(new ExpiryReconciler(db, triggers)),
    ]);
    return (db, repo, idx);
  }

  [Fact]
  public async Task Create_runs_all_three_behaviors_and_unique_enforcement_together()
  {
    var (db, repo, idx) = await SetupAsync();
    using var _ = db;

    var e = await repo.CreateAsync("wish",
      JsonNode.Parse("""{"name":"n1","content":"a red bike","expiresAt":"2026-12-01T00:00:00Z","dueAt":"2026-09-01T09:00:00Z"}"""), []);

    Assert.Equal("a red bike", idx.Store["wish"][e.Id]);                       // SemanticIndex
    Assert.Empty(await db.IndexOutbox.ToListAsync());                          // drained post-commit
    Assert.Single(await db.UniqueKeys.Where(k => k.EntityId == e.Id).ToListAsync()); // UniqueName
    var kinds = (await db.Triggers.Where(t => t.EntityId == e.Id).ToListAsync())
      .Select(t => (t.HandlerKind, t.Source)).ToHashSet();
    Assert.Contains(("notify", null), kinds);                         // TriggerProvisioning
    Assert.Contains(("delete", (string?)"expiry"), kinds);                     // Expiry

    await Assert.ThrowsAsync<TietueValidationException>(() =>                  // UniqueName coexists
      repo.CreateAsync("wish", JsonNode.Parse("""{"name":"n1"}"""), []));
  }

  private sealed class RecordingBehavior(List<string> log, Data.TietueDbContext db) : IEntityBehavior
  {
    public async Task OnSavingAsync(BehaviorContext ctx, CancellationToken ct)
    {
      using var fresh = TestDb.SameStore(db);
      log.Add($"OnSaving(saved:{await fresh.Entities.AnyAsync(e => e.Id == ctx.Entity.Id, ct)})");
    }

    public async Task OnSavedAsync(BehaviorContext ctx, CancellationToken ct)
    {
      using var fresh = TestDb.SameStore(db);
      log.Add($"OnSaved(saved:{await fresh.Entities.AnyAsync(e => e.Id == ctx.Entity.Id, ct)})");
    }

    public Task OnCommittedAsync(BehaviorContext ctx, CancellationToken ct)
    {
      log.Add("OnCommitted");
      return Task.CompletedTask;
    }
  }

  private sealed class ThrowingOnSavedBehavior : IEntityBehavior
  {
    public Task OnSavedAsync(BehaviorContext ctx, CancellationToken ct)
    {
      throw new InvalidOperationException("simulated provisioning failure");
    }
  }

  [Fact]
  public async Task Hooks_run_saving_saved_committed_around_the_save()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("note", /*lang=json,strict*/ """{"type":"object"}""");
    var log = new List<string>();
    var repo = new EntityRepository(db, new SchemaValidator(), [new RecordingBehavior(log, db)]);

    await repo.CreateAsync("note", JsonNode.Parse("{}"), []);

    Assert.Equal(["OnSaving(saved:False)", "OnSaved(saved:True)", "OnCommitted"], log);
  }

  [Fact]
  public async Task Failing_OnSaved_propagates_and_skips_OnCommitted()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("note", /*lang=json,strict*/ """{"type":"object"}""");
    var log = new List<string>();
    var repo = new EntityRepository(db, new SchemaValidator(),
      [new RecordingBehavior(log, db), new ThrowingOnSavedBehavior()]);

    await Assert.ThrowsAsync<InvalidOperationException>(() =>
      repo.CreateAsync("note", JsonNode.Parse("{}"), []));

    Assert.DoesNotContain("OnCommitted", log); // rollback path never reaches post-commit hooks
  }
}
