namespace toimi.tools.taidot.Skills;

public record SkillEntry(
    Guid Id,
    string Name,
    string Description,
    string Instructions,
    string[] Tags,
    DateTimeOffset CreatedAt,
    float? Score = null);
