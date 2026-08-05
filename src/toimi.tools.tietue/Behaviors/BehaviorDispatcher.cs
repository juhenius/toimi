using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Semantic;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Behaviors;

public record ScoredEntity(Entity Entity, float Score);

public class BehaviorDispatcher(TietueDbContext db, ISemanticIndex index)
{
  public async Task<IReadOnlyList<ScoredEntity>> SearchAsync(string type, string query, int limit, CancellationToken ct = default)
  {
    var cfg = await SemanticConfigAsync(type, ct)
      ?? throw new TietueValidationException([$"Type '{type}' is not semantically indexed (no SemanticIndex behavior)."]);

    var scored = await index.SearchAsync(type, query, limit, ct);
    if (scored.Count == 0)
    {
      return [];
    }

    var scoreById = scored.ToDictionary(s => s.EntityId, s => s.Score);
    var ids = scoreById.Keys.ToList();
    var entities = await db.Entities.Where(e => ids.Contains(e.Id)).ToListAsync(ct);

    return [.. entities
      .Select(e => new ScoredEntity(e, scoreById.GetValueOrDefault(e.Id)))
      .OrderByDescending(r => r.Score)];
  }

  private async Task<SemanticIndexConfig?> SemanticConfigAsync(string type, CancellationToken ct)
  {
    var typeDef = await db.TypeDefinitions.FirstOrDefaultAsync(t => t.Name == type, ct);
    return typeDef is null ? null : TypeBehaviors.Parse(typeDef.Behaviors).SemanticIndex;
  }
}
