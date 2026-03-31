namespace toimi.tools.muistio.Memory;

public record MemoryEntry(
    Guid Id,
    string Content,
    string? Category,
    string[] Tags,
    string Source,
    bool Confirmed,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    float? Score = null);
