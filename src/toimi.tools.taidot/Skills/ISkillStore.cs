namespace toimi.tools.taidot.Skills;

public interface ISkillStore
{
  Task<IReadOnlyList<SkillEntry>> ListAllAsync(CancellationToken ct = default);
  Task<SkillEntry?> GetByIdAsync(Guid id, CancellationToken ct = default);
  Task<bool> DeleteByIdAsync(Guid id, CancellationToken ct = default);
  Task UpsertPointAsync(Guid id, string name, string description, string instructions,
      string[] tags, float[] embedding, DateTimeOffset createdAt, CancellationToken ct = default);
}
