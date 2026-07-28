using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Semantic;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ReconcileTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""";
  private const string Behaviors = /*lang=json,strict*/ """[{"behavior":"SemanticIndex","config":{"fields":["name"]}}]""";

  private sealed class StubIndex : ISemanticIndex
  {
    public List<Guid> Ids { get; init; } = [];

    public Task EnsureCollectionAsync(string collection, CancellationToken ct = default)
    {
      return Task.CompletedTask;
    }

    public Task IndexAsync(string collection, Guid entityId, string text, CancellationToken ct = default)
    {
      return Task.CompletedTask;
    }

    public Task RemoveAsync(string collection, Guid entityId, CancellationToken ct = default)
    {
      return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScoredId>> SearchAsync(string collection, string query, int limit, CancellationToken ct = default)
    {
      return Task.FromResult<IReadOnlyList<ScoredId>>([]);
    }

    public Task<IReadOnlyList<Guid>> ListIdsAsync(string collection, CancellationToken ct = default)
    {
      return Task.FromResult<IReadOnlyList<Guid>>(Ids);
    }
  }

  [Fact]
  public async Task Enqueues_upserts_for_missing_and_deletes_for_orphans()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("memory", Schema, Behaviors);
    var repo = new EntityRepository(db, new SchemaValidator()); // no outbox: entities exist but were never indexed
    var e1 = await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"a"}"""), []);
    var e2 = await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"b"}"""), []);
    var orphan = Guid.NewGuid();
    var index = new StubIndex { Ids = [e2.Id, orphan] };

    var result = await SemanticReconciler.ReconcileAsync(db, index, "memory", default);

    Assert.Equal(1, result.MissingEnqueued);   // e1
    Assert.Equal(1, result.OrphansEnqueued);   // orphan
    var rows = await db.IndexOutbox.ToListAsync();
    Assert.Contains(rows, r => r.EntityId == e1.Id && r.Op == "upsert");
    Assert.Contains(rows, r => r.EntityId == orphan && r.Op == "delete");
    Assert.Equal(2, rows.Count);
  }

  [Fact]
  public async Task Reconcile_purges_dead_rows_for_the_type()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("memory", Schema, Behaviors);
    await new TypeRepository(db).DefineAsync("skill", Schema, Behaviors);

    var deadForMemory = new IndexOutbox
    {
      Id = Guid.NewGuid(),
      EntityId = Guid.NewGuid(),
      Type = "memory",
      Op = "upsert",
      Attempts = SemanticOutbox.MaxAttempts,
      CreatedAt = DateTimeOffset.UtcNow,
    };
    var deadForOtherType = new IndexOutbox
    {
      Id = Guid.NewGuid(),
      EntityId = Guid.NewGuid(),
      Type = "skill",
      Op = "upsert",
      Attempts = SemanticOutbox.MaxAttempts,
      CreatedAt = DateTimeOffset.UtcNow,
    };
    db.IndexOutbox.AddRange(deadForMemory, deadForOtherType);
    await db.SaveChangesAsync();

    var index = new StubIndex { Ids = [] };
    await SemanticReconciler.ReconcileAsync(db, index, "memory", default);

    var remaining = await db.IndexOutbox.ToListAsync();
    Assert.DoesNotContain(remaining, r => r.Id == deadForMemory.Id);
    Assert.Contains(remaining, r => r.Id == deadForOtherType.Id);
  }

  [Fact]
  public async Task Unindexed_type_is_rejected()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("plain", Schema); // no behaviors
    var index = new StubIndex();

    await Assert.ThrowsAsync<InvalidOperationException>(
      () => SemanticReconciler.ReconcileAsync(db, index, "plain", default));
    await Assert.ThrowsAsync<InvalidOperationException>(
      () => SemanticReconciler.ReconcileAsync(db, index, "nosuchtype", default));
  }
}
