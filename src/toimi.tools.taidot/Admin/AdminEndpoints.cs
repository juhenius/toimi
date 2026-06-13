using Toimi.Core.Admin;
using toimi.tools.taidot.Skills;

namespace toimi.tools.taidot.Admin;

public static class AdminEndpoints
{
  public record SkillItem(
      Guid Id, string Name, string Description, string Instructions,
      string[] Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

  public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Size, int Total);

  public record SkillUpdate(string Name, string Description, string Instructions, string[] Tags);

  public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
  {
    var admin = app.MapAdmin();

    admin.MapGet("/summary", async (SkillAdminRepository repo, string? q, int limit = 0, CancellationToken ct = default) =>
    {
      limit = limit <= 0 ? 50 : Math.Clamp(limit, 1, 200);
      var all = await repo.ListAsync(ct);
      var filtered = string.IsNullOrWhiteSpace(q)
        ? all
        : all.Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                      || s.Description.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
      return Results.Ok(filtered
        .OrderByDescending(s => s.UpdatedAt)
        .Take(limit)
        .Select(s => new AdminSummaryDto(
          s.Id.ToString(),
          "skill",
          s.Name,
          s.Description.Length > 80 ? s.Description[..80] : s.Description,
          s.CreatedAt,
          s.UpdatedAt))
        .ToList());
    });

    admin.MapGet("/items", async (SkillAdminRepository repo, string? q, int page = 0, int size = 0, CancellationToken ct = default) =>
    {
      page = page <= 0 ? 1 : page;
      size = size <= 0 ? 20 : Math.Clamp(size, 1, 100);
      var all = await repo.ListAsync(ct);
      var filtered = string.IsNullOrWhiteSpace(q)
        ? all
        : all.Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                      || s.Description.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
      var total = filtered.Count;
      var items = filtered
        .OrderByDescending(s => s.UpdatedAt)
        .Skip((page - 1) * size).Take(size)
        .Select(s => new SkillItem(s.Id, s.Name, s.Description, s.Instructions, s.Tags, s.CreatedAt, s.UpdatedAt))
        .ToList();
      return Results.Ok(new PagedResult<SkillItem>(items, page, size, total));
    });

    admin.MapGet("/items/{id:guid}", async (SkillAdminRepository repo, Guid id, CancellationToken ct) =>
    {
      var s = await repo.GetAsync(id, ct);
      return s is null
        ? Results.NotFound()
        : Results.Ok(new SkillItem(s.Id, s.Name, s.Description, s.Instructions, s.Tags, s.CreatedAt, s.UpdatedAt));
    });

    admin.MapPut("/items/{id:guid}", async (HttpContext ctx, SkillAdminRepository repo, Guid id, SkillUpdate body, CancellationToken ct) =>
    {
      if (!ctx.Request.Headers.TryGetValue("If-Unmodified-Since", out var ius))
        return Results.StatusCode(428);
      if (!DateTimeOffset.TryParse(ius.ToString(), out var clientUpdatedAt))
        return Results.BadRequest(new { error = "invalid-If-Unmodified-Since" });
      var existing = await repo.GetAsync(id, ct);
      if (existing is null) return Results.NotFound();
      if (Math.Abs((existing.UpdatedAt - clientUpdatedAt).TotalSeconds) > 1)
        return Results.Conflict(new { error = "stale", currentUpdatedAt = existing.UpdatedAt });

      var updated = await repo.UpdateAsync(id, body.Name, body.Description, body.Instructions,
          body.Tags ?? [], existing.CreatedAt, ct);
      return Results.Ok(new SkillItem(updated.Id, updated.Name, updated.Description,
          updated.Instructions, updated.Tags, updated.CreatedAt, updated.UpdatedAt));
    });

    admin.MapDelete("/items/{id:guid}", async (SkillAdminRepository repo, Guid id, CancellationToken ct) =>
    {
      var deleted = await repo.DeleteAsync(id, ct);
      return deleted ? Results.NoContent() : Results.NotFound();
    });
  }
}
