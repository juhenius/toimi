namespace toimi.tools.taidot.Skills;

public class SkillAdminRepository(ISkillStore store, EmbeddingService embeddings)
{
  public Task<IReadOnlyList<SkillEntry>> ListAsync(CancellationToken ct = default)
    => store.ListAllAsync(ct);

  public Task<SkillEntry?> GetAsync(Guid id, CancellationToken ct = default)
    => store.GetByIdAsync(id, ct);

  public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    => store.DeleteByIdAsync(id, ct);

  public async Task<SkillEntry> UpdateAsync(
      Guid id, string name, string description, string instructions,
      string[] tags, DateTimeOffset createdAt, CancellationToken ct = default)
  {
    var embedding = await embeddings.GenerateEmbeddingAsync($"{name}\n{description}\n{instructions}");
    await store.UpsertPointAsync(id, name, description, instructions, tags, embedding, createdAt, ct);
    return (await store.GetByIdAsync(id, ct))!;
  }
}
