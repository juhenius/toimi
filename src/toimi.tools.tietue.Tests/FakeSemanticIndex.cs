using toimi.tools.tietue.Semantic;

namespace toimi.tools.tietue.Tests;

public class FakeSemanticIndex : ISemanticIndex
{
  // collection -> (entityId -> indexed text)
  public Dictionary<string, Dictionary<Guid, string>> Store { get; } = [];
  public HashSet<string> EnsuredCollections { get; } = [];

  public Task EnsureCollectionAsync(string collection, CancellationToken ct = default)
  {
    EnsuredCollections.Add(collection);
    if (!Store.ContainsKey(collection))
    {
      Store[collection] = [];
    }

    return Task.CompletedTask;
  }

  public Task IndexAsync(string collection, Guid entityId, string text, CancellationToken ct = default)
  {
    if (!Store.TryGetValue(collection, out var c))
    {
      c = Store[collection] = [];
    }

    c[entityId] = text;
    return Task.CompletedTask;
  }

  public Task RemoveAsync(string collection, Guid entityId, CancellationToken ct = default)
  {
    if (Store.TryGetValue(collection, out var c))
    {
      c.Remove(entityId);
    }

    return Task.CompletedTask;
  }

  public Task<IReadOnlyList<ScoredId>> SearchAsync(string collection, string query, int limit, CancellationToken ct = default)
  {
    if (!Store.TryGetValue(collection, out var c))
    {
      return Task.FromResult<IReadOnlyList<ScoredId>>([]);
    }

    var ranked = c
      .Select(kvp => new ScoredId(kvp.Key, Overlap(kvp.Value, query)))
      .Where(s => s.Score > 0)
      .OrderByDescending(s => s.Score)
      .Take(limit)
      .ToList();

    return Task.FromResult<IReadOnlyList<ScoredId>>(ranked);
  }

  public Task<IReadOnlyList<Guid>> ListIdsAsync(string collection, CancellationToken ct = default)
  {
    return Task.FromResult<IReadOnlyList<Guid>>(
      Store.TryGetValue(collection, out var c) ? [.. c.Keys] : []);
  }

  private static float Overlap(string text, string query)
  {
    var t = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var q = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return q.Count(t.Contains);
  }
}
