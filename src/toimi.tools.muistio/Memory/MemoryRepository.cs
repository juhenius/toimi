using Microsoft.EntityFrameworkCore;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using toimi.tools.muistio.Data;

namespace toimi.tools.muistio.Memory;

public class MemoryRepository(MuistioDbContext dbContext, QdrantClient qdrant)
{
  private const string CollectionName = "memories";
  private const uint VectorSize = 1536;

  public async Task EnsureCollectionAsync(CancellationToken ct = default)
  {
    if (await qdrant.CollectionExistsAsync(CollectionName, ct))
    {
      return;
    }

    await qdrant.CreateCollectionAsync(
        CollectionName,
        new VectorParams { Size = VectorSize, Distance = Distance.Cosine },
        cancellationToken: ct);
  }

  public async Task<MemoryEntry> SaveAsync(
      string content,
      float[] embedding,
      string? category,
      string[] tags,
      string source,
      bool confirmed,
      DateTimeOffset? expiresAt,
      CancellationToken ct = default)
  {
    var now = DateTimeOffset.UtcNow;
    var memory = new Data.Memory
    {
      Content = content,
      Category = category,
      Tags = tags,
      Source = source,
      Confirmed = confirmed,
      ExpiresAt = expiresAt,
      CreatedAt = now,
      UpdatedAt = now,
    };

    dbContext.Memories.Add(memory);
    await dbContext.SaveChangesAsync(ct);

    var point = new PointStruct { Id = memory.Id, Vectors = embedding };
    point.Payload["memory_id"] = memory.Id.ToString();

    await qdrant.UpsertAsync(CollectionName, [point], cancellationToken: ct);

    return ToEntry(memory);
  }

  public async Task<IReadOnlyList<MemoryEntry>> RecallAsync(
      float[] queryEmbedding,
      int limit = 10,
      string? category = null,
      string[]? tags = null,
      CancellationToken ct = default)
  {
    var searchLimit = (ulong)(limit * 3);
    var results = await qdrant.SearchAsync(
        CollectionName,
        queryEmbedding,
        limit: searchLimit,
        cancellationToken: ct);

    if (results.Count == 0)
    {
      return [];
    }

    var scoreMap = results.ToDictionary(
        r => Guid.Parse(r.Id.Uuid),
        r => r.Score);

    var ids = scoreMap.Keys.ToList();

    var query = dbContext.Memories
        .Where(m => ids.Contains(m.Id))
        .Where(m => m.ExpiresAt == null || m.ExpiresAt > DateTimeOffset.UtcNow);

    if (category is not null)
    {
      query = query.Where(m => m.Category == category);
    }

    if (tags is { Length: > 0 })
    {
      query = query.Where(m => tags.All(t => m.Tags.Contains(t)));
    }

    var memories = await query.ToListAsync(ct);

    return [.. memories
        .Select(m => ToEntry(m, scoreMap.GetValueOrDefault(m.Id)))
        .OrderByDescending(e => e.Score)
        .Take(limit)];
  }

  public async Task<MemoryEntry?> GetByIdAsync(Guid id, CancellationToken ct = default)
  {
    var memory = await dbContext.Memories.FindAsync([id], ct);
    return memory is null ? null : ToEntry(memory);
  }

  public async Task<IReadOnlyList<MemoryEntry>> ListAsync(
      int limit = 20,
      int offset = 0,
      string? category = null,
      string[]? tags = null,
      bool includeExpired = false,
      CancellationToken ct = default)
  {
    var query = dbContext.Memories.AsQueryable();

    if (!includeExpired)
    {
      query = query.Where(m => m.ExpiresAt == null || m.ExpiresAt > DateTimeOffset.UtcNow);
    }

    if (category is not null)
    {
      query = query.Where(m => m.Category == category);
    }

    if (tags is { Length: > 0 })
    {
      query = query.Where(m => tags.All(t => m.Tags.Contains(t)));
    }

    var memories = await query
        .OrderByDescending(m => m.CreatedAt)
        .Skip(offset)
        .Take(limit)
        .ToListAsync(ct);

    return [.. memories.Select(m => ToEntry(m))];
  }

  public async Task<MemoryEntry?> UpdateAsync(
      Guid id,
      string? content = null,
      string? category = null,
      string[]? tags = null,
      bool? confirmed = null,
      DateTimeOffset? expiresAt = null,
      float[]? embedding = null,
      CancellationToken ct = default)
  {
    var memory = await dbContext.Memories.FindAsync([id], ct);
    if (memory is null)
    {
      return null;
    }

    var contentChanged = false;
    if (content is not null)
    {
      memory.Content = content;
      contentChanged = true;
    }

    if (category is not null)
    {
      memory.Category = category;
    }

    if (tags is not null)
    {
      memory.Tags = tags;
    }

    if (confirmed.HasValue)
    {
      memory.Confirmed = confirmed.Value;
    }

    if (expiresAt.HasValue)
    {
      memory.ExpiresAt = expiresAt.Value;
    }

    memory.UpdatedAt = DateTimeOffset.UtcNow;
    await dbContext.SaveChangesAsync(ct);

    if (contentChanged && embedding is not null)
    {
      var point = new PointStruct { Id = memory.Id, Vectors = embedding };
      point.Payload["memory_id"] = memory.Id.ToString();
      await qdrant.UpsertAsync(CollectionName, [point], cancellationToken: ct);
    }

    return ToEntry(memory);
  }

  public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
  {
    var memory = await dbContext.Memories.FindAsync([id], ct);
    if (memory is null)
    {
      return false;
    }

    dbContext.Memories.Remove(memory);
    await dbContext.SaveChangesAsync(ct);

    await qdrant.DeleteAsync(CollectionName, id, cancellationToken: ct);
    return true;
  }

  public async Task<int> RebuildIndexAsync(
      EmbeddingService embeddingService,
      CancellationToken ct = default)
  {
    var memories = await dbContext.Memories.ToListAsync(ct);

    if (await qdrant.CollectionExistsAsync(CollectionName, ct))
    {
      await qdrant.DeleteCollectionAsync(CollectionName, cancellationToken: ct);
    }

    await qdrant.CreateCollectionAsync(
        CollectionName,
        new VectorParams { Size = VectorSize, Distance = Distance.Cosine },
        cancellationToken: ct);

    foreach (var memory in memories)
    {
      var embedding = await embeddingService.GenerateEmbeddingAsync(memory.Content);
      var point = new PointStruct { Id = memory.Id, Vectors = embedding };
      point.Payload["memory_id"] = memory.Id.ToString();
      await qdrant.UpsertAsync(CollectionName, [point], cancellationToken: ct);
    }

    return memories.Count;
  }

  private static MemoryEntry ToEntry(Data.Memory memory, float? score = null)
  {
    return new(
          memory.Id,
          memory.Content,
          memory.Category,
          memory.Tags,
          memory.Source,
          memory.Confirmed,
          memory.ExpiresAt,
          memory.CreatedAt,
          memory.UpdatedAt,
          score);
  }
}
