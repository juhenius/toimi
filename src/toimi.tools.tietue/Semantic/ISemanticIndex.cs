namespace toimi.tools.tietue.Semantic;

public record ScoredId(Guid EntityId, float Score);

public interface ISemanticIndex
{
  Task EnsureCollectionAsync(string collection, CancellationToken ct = default);

  Task IndexAsync(string collection, Guid entityId, string text, CancellationToken ct = default);

  Task RemoveAsync(string collection, Guid entityId, CancellationToken ct = default);

  // Embeds the query internally and returns entity ids ranked by similarity, deduped by entity (best score wins).
  Task<IReadOnlyList<ScoredId>> SearchAsync(string collection, string query, int limit, CancellationToken ct = default);

  // Returns every point id in the collection (for reconciliation). Empty if the collection doesn't exist.
  Task<IReadOnlyList<Guid>> ListIdsAsync(string collection, CancellationToken ct = default);
}
