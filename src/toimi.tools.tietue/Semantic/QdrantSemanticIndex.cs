using Microsoft.Extensions.AI;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace toimi.tools.tietue.Semantic;

public class QdrantSemanticIndex(QdrantClient qdrant, IEmbeddingGenerator<string, Embedding<float>> embeddings) : ISemanticIndex
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
    var embedding = (await embeddings.GenerateVectorAsync(text, cancellationToken: ct)).ToArray();
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

    var embedding = (await embeddings.GenerateVectorAsync(query, cancellationToken: ct)).ToArray();
    var results = await qdrant.SearchAsync(collection, embedding, limit: (ulong)limit, cancellationToken: ct);

    // Roll up by entity id (best score wins) — one point per entity today, but keeps the contract stable for future chunking.
    return [.. results
      .GroupBy(r => Guid.Parse(r.Id.Uuid))
      .Select(g => new ScoredId(g.Key, g.Max(r => r.Score)))
      .OrderByDescending(s => s.Score)];
  }

  public async Task<IReadOnlyList<Guid>> ListIdsAsync(string collection, CancellationToken ct = default)
  {
    if (!await qdrant.CollectionExistsAsync(collection, ct))
    {
      return [];
    }

    var ids = new List<Guid>();
    PointId? offset = null;
    while (true)
    {
      var page = await qdrant.ScrollAsync(collection, limit: 256, offset: offset, payloadSelector: new WithPayloadSelector { Enable = false }, cancellationToken: ct);
      ids.AddRange(page.Result.Select(p => Guid.Parse(p.Id.Uuid)));
      if (page.NextPageOffset is null || page.Result.Count == 0)
      {
        break;
      }

      offset = page.NextPageOffset;
    }

    return ids;
  }
}
