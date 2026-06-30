using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace toimi.tools.tietue.Semantic;

public class QdrantSemanticIndex(QdrantClient qdrant, EmbeddingService embeddings) : ISemanticIndex
{
  private const uint VectorSize = 1536;

  public async Task EnsureCollectionAsync(string collection, CancellationToken ct = default)
  {
    if (await qdrant.CollectionExistsAsync(collection, ct))
    {
      return;
    }

    await qdrant.CreateCollectionAsync(
      collection,
      new VectorParams { Size = VectorSize, Distance = Distance.Cosine },
      cancellationToken: ct);
  }

  public async Task IndexAsync(string collection, Guid entityId, string text, CancellationToken ct = default)
  {
    var embedding = await embeddings.GenerateEmbeddingAsync(text);
    var point = new PointStruct { Id = entityId, Vectors = embedding };
    point.Payload["entity_id"] = entityId.ToString();
    await qdrant.UpsertAsync(collection, [point], cancellationToken: ct);
  }

  public async Task RemoveAsync(string collection, Guid entityId, CancellationToken ct = default)
  {
    await qdrant.DeleteAsync(collection, entityId, cancellationToken: ct);
  }

  public async Task<IReadOnlyList<ScoredId>> SearchAsync(string collection, string query, int limit, CancellationToken ct = default)
  {
    if (!await qdrant.CollectionExistsAsync(collection, ct))
    {
      return [];
    }

    var embedding = await embeddings.GenerateEmbeddingAsync(query);
    var results = await qdrant.SearchAsync(collection, embedding, limit: (ulong)limit, cancellationToken: ct);

    // Roll up by entity id (best score wins) — one point per entity today, but keeps the contract stable for future chunking.
    return [.. results
      .GroupBy(r => Guid.Parse(r.Id.Uuid))
      .Select(g => new ScoredId(g.Key, g.Max(r => r.Score)))
      .OrderByDescending(s => s.Score)];
  }
}
