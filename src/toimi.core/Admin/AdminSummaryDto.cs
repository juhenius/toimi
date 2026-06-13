namespace Toimi.Core.Admin;

public record AdminSummaryDto(
    string Id,
    string Kind,
    string Title,
    string? Subtitle,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
