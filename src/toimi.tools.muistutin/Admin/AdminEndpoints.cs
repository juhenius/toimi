using Microsoft.EntityFrameworkCore;
using Toimi.Core.Admin;
using toimi.tools.muistutin.Data;

namespace toimi.tools.muistutin.Admin;

public static class AdminEndpoints
{
  public record ReminderItem(
      Guid Id, string Title, string? Description,
      DateTimeOffset DateTimeUtc, string TimeZone,
      string? RecurrenceRule, DateTimeOffset? DisplayEndUtc,
      bool IsCompleted, DateTimeOffset? NotifiedAt,
      DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

  public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Size, int Total);

  public record ReminderUpdate(string? Title, string? Description,
      DateTimeOffset? DateTimeUtc, string? TimeZone,
      string? RecurrenceRule, DateTimeOffset? DisplayEndUtc, bool? IsCompleted);

  public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
  {
    var admin = app.MapAdmin();

    admin.MapGet("/summary", async (MuistutinDbContext db, string? q, int limit = 0) =>
    {
      limit = limit <= 0 ? 50 : Math.Clamp(limit, 1, 200);
      var query = db.Reminders.AsQueryable();
      if (!string.IsNullOrWhiteSpace(q))
      {
        var pattern = $"%{q}%";
        query = query.Where(r => EF.Functions.ILike(r.Title, pattern));
      }
      var rows = await query
        .OrderByDescending(r => r.UpdatedAt)
        .Take(limit)
        .Select(r => new AdminSummaryDto(
          r.Id.ToString(),
          "reminder",
          r.Title,
          (r.IsCompleted ? "completed — " : "") + r.DateTimeUtc.ToString("u")
            + (r.RecurrenceRule != null ? " (recurring)" : ""),
          r.CreatedAt,
          r.UpdatedAt))
        .ToListAsync();
      return Results.Ok(rows);
    });

    admin.MapGet("/items", async (MuistutinDbContext db, string? q, int page = 0, int size = 0) =>
    {
      page = page <= 0 ? 1 : page;
      size = size <= 0 ? 20 : Math.Clamp(size, 1, 100);
      var query = db.Reminders.AsQueryable();
      if (!string.IsNullOrWhiteSpace(q))
      {
        var pattern = $"%{q}%";
        query = query.Where(r => EF.Functions.ILike(r.Title, pattern));
      }
      var total = await query.CountAsync();
      var items = await query
        .OrderByDescending(r => r.UpdatedAt)
        .Skip((page - 1) * size).Take(size)
        .Select(r => new ReminderItem(r.Id, r.Title, r.Description, r.DateTimeUtc, r.TimeZone,
          r.RecurrenceRule, r.DisplayEndUtc, r.IsCompleted, r.NotifiedAt, r.CreatedAt, r.UpdatedAt))
        .ToListAsync();
      return Results.Ok(new PagedResult<ReminderItem>(items, page, size, total));
    });

    admin.MapGet("/items/{id:guid}", async (MuistutinDbContext db, Guid id) =>
    {
      var r = await db.Reminders.FindAsync(id);
      return r is null
        ? Results.NotFound()
        : Results.Ok(new ReminderItem(r.Id, r.Title, r.Description, r.DateTimeUtc, r.TimeZone,
          r.RecurrenceRule, r.DisplayEndUtc, r.IsCompleted, r.NotifiedAt, r.CreatedAt, r.UpdatedAt));
    });

    admin.MapPut("/items/{id:guid}", async (HttpContext ctx, MuistutinDbContext db, Guid id, ReminderUpdate body) =>
    {
      if (!ctx.Request.Headers.TryGetValue("If-Unmodified-Since", out var ius))
        return Results.StatusCode(428);
      if (!DateTimeOffset.TryParse(ius.ToString(), out var clientUpdatedAt))
        return Results.BadRequest(new { error = "invalid-If-Unmodified-Since" });
      var r = await db.Reminders.FindAsync(id);
      if (r is null) return Results.NotFound();
      if (Math.Abs((r.UpdatedAt - clientUpdatedAt).TotalSeconds) > 1)
        return Results.Conflict(new { error = "stale", currentUpdatedAt = r.UpdatedAt });

      if (body.Title is not null) r.Title = body.Title;
      if (body.Description is not null) r.Description = body.Description;
      if (body.DateTimeUtc is not null) r.DateTimeUtc = body.DateTimeUtc.Value;
      if (body.TimeZone is not null) r.TimeZone = body.TimeZone;
      if (body.RecurrenceRule is not null) r.RecurrenceRule = body.RecurrenceRule;
      if (body.DisplayEndUtc is not null) r.DisplayEndUtc = body.DisplayEndUtc;
      if (body.IsCompleted is not null) r.IsCompleted = body.IsCompleted.Value;
      r.UpdatedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync();

      return Results.Ok(new ReminderItem(r.Id, r.Title, r.Description, r.DateTimeUtc, r.TimeZone,
        r.RecurrenceRule, r.DisplayEndUtc, r.IsCompleted, r.NotifiedAt, r.CreatedAt, r.UpdatedAt));
    });

    admin.MapDelete("/items/{id:guid}", async (MuistutinDbContext db, Guid id) =>
    {
      var r = await db.Reminders.FindAsync(id);
      if (r is null) return Results.NotFound();
      db.Reminders.Remove(r);
      await db.SaveChangesAsync();
      return Results.NoContent();
    });

    admin.MapPost("/items/{id:guid}/complete", async (MuistutinDbContext db, Guid id) =>
    {
      var r = await db.Reminders.FindAsync(id);
      if (r is null) return Results.NotFound();
      r.IsCompleted = true;
      r.UpdatedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync();
      return Results.NoContent();
    });
  }
}
