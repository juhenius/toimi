using System.Globalization;
using Google.Protobuf.Collections;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace toimi.tools.taidot.Skills;

public class SkillRepository(QdrantClient qdrant)
{
  private const string CollectionName = "skills";
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

  public async Task<Guid> UpsertAsync(
      string name,
      string description,
      string instructions,
      string[] tags,
      float[] embedding,
      CancellationToken ct = default)
  {
    // Delete any existing point with the same name
    var existing = await FindByNameAsync(name, ct);
    if (existing is not null)
    {
      await qdrant.DeleteAsync(CollectionName, existing.Value, cancellationToken: ct);
    }

    var id = Guid.NewGuid();
    var payload = new Dictionary<string, Value>
    {
      ["name"] = name,
      ["description"] = description,
      ["instructions"] = instructions,
      ["created_at"] = DateTimeOffset.UtcNow.ToString("o"),
    };

    if (tags.Length > 0)
    {
      payload["tags"] = tags;
    }

    var point = new PointStruct { Id = id, Vectors = embedding };
    foreach (var kvp in payload)
    {
      point.Payload[kvp.Key] = kvp.Value;
    }

    await qdrant.UpsertAsync(CollectionName, [point], cancellationToken: ct);

    return id;
  }

  public async Task<SkillEntry?> GetByNameAsync(string name, CancellationToken ct = default)
  {
    var filter = new Filter();
    filter.Must.Add(Conditions.MatchKeyword("name", name));

    var response = await qdrant.ScrollAsync(
        CollectionName,
        filter: filter,
        limit: 1,
        cancellationToken: ct);

    var point = response.Result.FirstOrDefault();
    return point is null ? null : ToSkillEntry(point.Id, point.Payload);
  }

  public async Task<IReadOnlyList<SkillEntry>> SearchAsync(
      float[] queryEmbedding,
      int limit = 10,
      string[]? tags = null,
      CancellationToken ct = default)
  {
    var filter = BuildFilter(tags);

    var results = await qdrant.SearchAsync(
        CollectionName,
        queryEmbedding,
        filter: filter,
        limit: (ulong)limit,
        cancellationToken: ct);

    return [.. results.Select(r => ToSkillEntry(r.Id, r.Payload, r.Score))];
  }

  public async Task<IReadOnlyList<SkillEntry>> ListAsync(
      int limit = 20,
      int offset = 0,
      string[]? tags = null,
      CancellationToken ct = default)
  {
    var filter = BuildFilter(tags);

    var response = await qdrant.ScrollAsync(
        CollectionName,
        filter: filter,
        limit: (uint)(limit + offset),
        cancellationToken: ct);

    return [.. response.Result
        .Skip(offset)
        .Take(limit)
        .Select(r => ToSkillEntry(r.Id, r.Payload))];
  }

  public async Task<bool> DeleteByNameAsync(string name, CancellationToken ct = default)
  {
    var existing = await FindByNameAsync(name, ct);
    if (existing is null)
    {
      return false;
    }

    await qdrant.DeleteAsync(CollectionName, existing.Value, cancellationToken: ct);
    return true;
  }

  private async Task<Guid?> FindByNameAsync(string name, CancellationToken ct)
  {
    var filter = new Filter();
    filter.Must.Add(Conditions.MatchKeyword("name", name));

    var response = await qdrant.ScrollAsync(
        CollectionName,
        filter: filter,
        limit: 1,
        cancellationToken: ct);

    var point = response.Result.FirstOrDefault();
    return point is null ? null : Guid.Parse(point.Id.Uuid);
  }

  private static Filter? BuildFilter(string[]? tags)
  {
    if (tags is not { Length: > 0 })
    {
      return null;
    }

    var filter = new Filter();
    foreach (var tag in tags)
    {
      filter.Must.Add(Conditions.MatchKeyword("tags", tag));
    }

    return filter;
  }

  private static SkillEntry ToSkillEntry(
      PointId pointId,
      MapField<string, Value> payload,
      float? score = null)
  {
    var id = Guid.Parse(pointId.Uuid);

    var name = payload.TryGetValue("name", out var nv)
        ? nv.StringValue
        : string.Empty;

    var description = payload.TryGetValue("description", out var dv)
        ? dv.StringValue
        : string.Empty;

    var instructions = payload.TryGetValue("instructions", out var iv)
        ? iv.StringValue
        : string.Empty;

    var entryTags = payload.TryGetValue("tags", out var tv)
        ? tv.ListValue.Values.Select(v => v.StringValue).ToArray()
        : [];

    var createdAt = payload.TryGetValue("created_at", out var dtV)
        ? DateTimeOffset.Parse(dtV.StringValue, CultureInfo.InvariantCulture)
        : DateTimeOffset.MinValue;

    return new SkillEntry(id, name, description, instructions, entryTags, createdAt, score);
  }
}
