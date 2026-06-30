using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Handlers;

public record HandlerContext(Entity Entity, string? ConfigJson, DateTimeOffset OccurrenceUtc);
