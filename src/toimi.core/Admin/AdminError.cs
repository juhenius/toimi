namespace Toimi.Core.Admin;

public record AdminError(string Tool, string Message);

public record AggregatedSummary(
    IReadOnlyList<AdminSummaryDto> Items,
    IReadOnlyList<AdminError> Errors);
