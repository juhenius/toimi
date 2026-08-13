using System.Text.Json;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Handlers;

/// <summary>
/// Params are the firing's call-time arguments (webhook query+body, or run_trigger's params
/// argument) — null for plain time-anchored firings. Always a detached JSON object element.
/// </summary>
public record HandlerContext(Entity Entity, string? ConfigJson, DateTimeOffset OccurrenceUtc, JsonElement? Params = null);
