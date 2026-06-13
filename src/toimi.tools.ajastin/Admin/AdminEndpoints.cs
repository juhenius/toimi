using Microsoft.EntityFrameworkCore;
using Toimi.Core.Admin;
using toimi.tools.ajastin.Data;

namespace toimi.tools.ajastin.Admin;

public static class AdminEndpoints
{
  public record ScheduleItem(
      Guid Id, string Name, string? CronExpression, DateTimeOffset? RunAt,
      string Prompt, bool Enabled, DateTimeOffset? LastRunAt,
      DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

  public record RunItem(
      Guid Id, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
      bool Success, string? Response, string? Error);

  public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Size, int Total);

  public record ScheduleUpdate(string? Name, string? CronExpression, DateTimeOffset? RunAt,
      string? Prompt, bool? Enabled);

  public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
  {
    var admin = app.MapAdmin();

    admin.MapGet("/summary", async (AjastinDbContext db, string? q, int limit = 0) =>
    {
      limit = limit <= 0 ? 50 : Math.Clamp(limit, 1, 200);
      var query = db.Schedules.AsQueryable();
      if (!string.IsNullOrWhiteSpace(q))
      {
        var pattern = $"%{q}%";
        query = query.Where(s => EF.Functions.ILike(s.Name, pattern));
      }
      var rows = await query
        .OrderByDescending(s => s.UpdatedAt)
        .Take(limit)
        .Select(s => new AdminSummaryDto(
          s.Id.ToString(),
          "schedule",
          s.Name,
          (s.CronExpression ?? (s.RunAt != null ? "one-shot " + s.RunAt.Value.ToString("u") : "no trigger"))
            + (s.Enabled ? "" : " (disabled)"),
          s.CreatedAt,
          s.UpdatedAt))
        .ToListAsync();
      return Results.Ok(rows);
    });

    admin.MapGet("/items", async (AjastinDbContext db, string? q, int page = 0, int size = 0) =>
    {
      page = page <= 0 ? 1 : page;
      size = size <= 0 ? 20 : Math.Clamp(size, 1, 100);
      var query = db.Schedules.AsQueryable();
      if (!string.IsNullOrWhiteSpace(q))
      {
        var pattern = $"%{q}%";
        query = query.Where(s => EF.Functions.ILike(s.Name, pattern));
      }
      var total = await query.CountAsync();
      var items = await query
        .OrderByDescending(s => s.UpdatedAt)
        .Skip((page - 1) * size).Take(size)
        .Select(s => new ScheduleItem(s.Id, s.Name, s.CronExpression, s.RunAt, s.Prompt,
          s.Enabled, s.LastRunAt, s.CreatedAt, s.UpdatedAt))
        .ToListAsync();
      return Results.Ok(new PagedResult<ScheduleItem>(items, page, size, total));
    });

    admin.MapGet("/items/{id:guid}", async (AjastinDbContext db, Guid id) =>
    {
      var s = await db.Schedules.FindAsync(id);
      return s is null
        ? Results.NotFound()
        : Results.Ok(new ScheduleItem(s.Id, s.Name, s.CronExpression, s.RunAt, s.Prompt,
            s.Enabled, s.LastRunAt, s.CreatedAt, s.UpdatedAt));
    });

    admin.MapGet("/items/{id:guid}/runs", async (AjastinDbContext db, Guid id, int limit = 0) =>
    {
      limit = limit <= 0 ? 20 : Math.Clamp(limit, 1, 100);
      var rows = await db.ScheduleRuns
        .Where(r => r.ScheduleId == id)
        .OrderByDescending(r => r.StartedAt)
        .Take(limit)
        .Select(r => new RunItem(r.Id, r.StartedAt, r.CompletedAt, r.Success, r.Response, r.Error))
        .ToListAsync();
      return Results.Ok(rows);
    });

    admin.MapPut("/items/{id:guid}", async (HttpContext ctx, AjastinDbContext db, Guid id, ScheduleUpdate body) =>
    {
      if (!ctx.Request.Headers.TryGetValue("If-Unmodified-Since", out var ius))
        return Results.StatusCode(428);
      if (!DateTimeOffset.TryParse(ius.ToString(), out var clientUpdatedAt))
        return Results.BadRequest(new { error = "invalid-If-Unmodified-Since" });
      var s = await db.Schedules.FindAsync(id);
      if (s is null) return Results.NotFound();
      if (Math.Abs((s.UpdatedAt - clientUpdatedAt).TotalSeconds) > 1)
        return Results.Conflict(new { error = "stale", currentUpdatedAt = s.UpdatedAt });

      if (body.Name is not null) s.Name = body.Name;
      if (body.CronExpression is not null) s.CronExpression = body.CronExpression;
      if (body.RunAt is not null) s.RunAt = body.RunAt;
      if (body.Prompt is not null) s.Prompt = body.Prompt;
      if (body.Enabled is not null) s.Enabled = body.Enabled.Value;
      s.UpdatedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync();

      return Results.Ok(new ScheduleItem(s.Id, s.Name, s.CronExpression, s.RunAt, s.Prompt,
        s.Enabled, s.LastRunAt, s.CreatedAt, s.UpdatedAt));
    });

    admin.MapDelete("/items/{id:guid}", async (AjastinDbContext db, Guid id) =>
    {
      var s = await db.Schedules.FindAsync(id);
      if (s is null) return Results.NotFound();
      db.Schedules.Remove(s);
      await db.SaveChangesAsync();
      return Results.NoContent();
    });

    admin.MapPost("/items/{id:guid}/enable", async (AjastinDbContext db, Guid id) =>
    {
      var s = await db.Schedules.FindAsync(id);
      if (s is null) return Results.NotFound();
      s.Enabled = true;
      s.UpdatedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync();
      return Results.NoContent();
    });

    admin.MapPost("/items/{id:guid}/disable", async (AjastinDbContext db, Guid id) =>
    {
      var s = await db.Schedules.FindAsync(id);
      if (s is null) return Results.NotFound();
      s.Enabled = false;
      s.UpdatedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync();
      return Results.NoContent();
    });

    admin.MapPost("/items/{id:guid}/run-now", async (AjastinDbContext db, Guid id) =>
    {
      var s = await db.Schedules.FindAsync(id);
      if (s is null) return Results.NotFound();
      s.RunAt = DateTimeOffset.UtcNow;
      s.UpdatedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync();
      return Results.NoContent();
    });
  }
}
