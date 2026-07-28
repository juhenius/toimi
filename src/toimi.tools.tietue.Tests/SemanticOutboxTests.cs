using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Semantic;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class SemanticOutboxTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"name":{"type":"string"},"content":{"type":"string"}},"required":["name"]}""";
  private const string Behaviors = /*lang=json,strict*/ """[{"behavior":"SemanticIndex","config":{"fields":["name","content"]}}]""";

  private sealed class RecordingIndex : ISemanticIndex
  {
    public List<(string Collection, Guid Id, string Text)> Indexed { get; } = [];
    public List<(string Collection, Guid Id)> Removed { get; } = [];
    public bool Fail { get; set; }

    public Task EnsureCollectionAsync(string collection, CancellationToken ct = default)
    {
      return Task.CompletedTask;
    }

    public Task IndexAsync(string collection, Guid entityId, string text, CancellationToken ct = default)
    {
      if (Fail)
      {
        throw new InvalidOperationException("qdrant down");
      }

      Indexed.Add((collection, entityId, text));
      return Task.CompletedTask;
    }

    public Task RemoveAsync(string collection, Guid entityId, CancellationToken ct = default)
    {
      if (Fail)
      {
        throw new InvalidOperationException("qdrant down");
      }

      Removed.Add((collection, entityId));
      return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScoredId>> SearchAsync(string collection, string query, int limit, CancellationToken ct = default)
    {
      return Task.FromResult<IReadOnlyList<ScoredId>>([]);
    }

    public Task<IReadOnlyList<Guid>> ListIdsAsync(string collection, CancellationToken ct = default)
    {
      return Task.FromResult<IReadOnlyList<Guid>>([]);
    }
  }

  private static async Task<(Data.TietueDbContext db, RecordingIndex index, EntityRepository repo)> SetupAsync()
  {
    var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("memory", Schema, Behaviors);
    var index = new RecordingIndex();
    var outbox = new SemanticOutbox(db, index);
    var repo = new EntityRepository(db, new SchemaValidator(), outbox);
    return (db, index, repo);
  }

  [Fact]
  public async Task Create_indexes_inline_and_leaves_no_outbox_row()
  {
    var (db, index, repo) = await SetupAsync();
    using var _ = db;

    var e = await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"n1","content":"hello"}"""), []);

    Assert.Single(index.Indexed);
    Assert.Equal(e.Id, index.Indexed[0].Id);
    Assert.Contains("hello", index.Indexed[0].Text);
    Assert.Empty(await db.IndexOutbox.ToListAsync());
  }

  [Fact]
  public async Task Failed_inline_index_leaves_retryable_outbox_row()
  {
    var (db, index, repo) = await SetupAsync();
    using var _ = db;
    index.Fail = true;

    var e = await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"n1"}"""), []);

    var row = await db.IndexOutbox.SingleAsync();
    Assert.Equal(e.Id, row.EntityId);
    Assert.Equal("upsert", row.Op);
    Assert.Equal(1, row.Attempts);
    Assert.Contains("qdrant down", row.LastError);
  }

  [Fact]
  public async Task Delete_enqueues_and_drains_a_delete_op()
  {
    var (db, index, repo) = await SetupAsync();
    using var _ = db;
    var e = await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"n1"}"""), []);

    await repo.DeleteAsync(e.Id);

    Assert.Single(index.Removed);
    Assert.Equal(e.Id, index.Removed[0].Id);
    Assert.Empty(await db.IndexOutbox.ToListAsync());
  }

  [Fact]
  public async Task Unindexed_type_enqueues_nothing()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("plain", Schema); // no behaviors
    var index = new RecordingIndex();
    var repo = new EntityRepository(db, new SchemaValidator(), new SemanticOutbox(db, index));

    await repo.CreateAsync("plain", JsonNode.Parse("""{"name":"n1"}"""), []);

    Assert.Empty(index.Indexed);
    Assert.Empty(await db.IndexOutbox.ToListAsync());
  }

  [Fact]
  public async Task Processing_upsert_for_deleted_entity_is_dropped_as_success()
  {
    var (db, index, _) = await SetupAsync();
    using var _2 = db;
    var outbox = new SemanticOutbox(db, index);
    var row = new Data.IndexOutbox { Id = Guid.NewGuid(), EntityId = Guid.NewGuid(), Type = "memory", Op = "upsert", CreatedAt = DateTimeOffset.UtcNow };

    await outbox.ProcessAsync(row); // must not throw

    Assert.Empty(index.Indexed);
  }

  [Fact]
  public async Task Failed_inline_delete_leaves_retryable_delete_row()
  {
    var (db, index, repo) = await SetupAsync();
    using var _ = db;

    var e = await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"n1","content":"hello"}"""), []);
    index.Indexed.Clear();
    index.Fail = true;

    await repo.DeleteAsync(e.Id);

    Assert.Empty(await db.Entities.ToListAsync());
    var row = await db.IndexOutbox.SingleAsync();
    Assert.Equal(e.Id, row.EntityId);
    Assert.Equal("delete", row.Op);
    Assert.Equal(1, row.Attempts);
    Assert.NotNull(row.LastError);
    Assert.Contains("qdrant down", row.LastError);
  }

  [Fact]
  public async Task Failed_create_validation_persists_no_outbox_row()
  {
    var (db, index, repo) = await SetupAsync();
    using var _ = db;

    // "name" is required; omitting it should fail schema validation.
    await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.CreateAsync("memory", JsonNode.Parse("""{"content":"no name here"}"""), []));

    Assert.Empty(await db.Entities.ToListAsync());
    Assert.Empty(await db.IndexOutbox.ToListAsync());
  }

  [Fact]
  public async Task Tags_only_update_enqueues_nothing()
  {
    var (db, index, repo) = await SetupAsync();
    using var _ = db;

    var e = await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"n1","content":"hello"}"""), []);
    index.Indexed.Clear();

    await repo.UpdateAsync(e.Id, data: null, tags: ["x"]);

    Assert.Empty(index.Indexed);
    Assert.Empty(await db.IndexOutbox.ToListAsync());
  }
}
