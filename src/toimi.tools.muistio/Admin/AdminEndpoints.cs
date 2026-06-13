using Microsoft.EntityFrameworkCore;
using Toimi.Core.Admin;
using toimi.tools.muistio.Data;

namespace toimi.tools.muistio.Admin;

public static class AdminEndpoints
{
  public record MemoryItem(
      Guid Id, string Content, string? Category, string[] Tags, string Source,
      bool Confirmed, DateTimeOffset? ExpiresAt,
      DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

  public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Size, int Total);

  public record MemoryUpdate(string? Content, string? Category, string[]? Tags, bool? Confirmed, DateTimeOffset? ExpiresAt);

  public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
  {
    var admin = app.MapAdmin();

    admin.MapGet("/summary", async (MuistioDbContext db, string? q, int limit = 0) =>
    {
      limit = limit <= 0 ? 50 : Math.Clamp(limit, 1, 200);
      var query = db.Memories.AsQueryable();
      if (!string.IsNullOrWhiteSpace(q))
      {
        var pattern = $"%{q}%";
        query = query.Where(m => EF.Functions.ILike(m.Content, pattern));
      }
      var rows = await query
        .OrderByDescending(m => m.UpdatedAt)
        .Take(limit)
        .Select(m => new AdminSummaryDto(
          m.Id.ToString(),
          "memory",
          m.Content.Length > 60 ? m.Content.Substring(0, 60) : m.Content,
          $"from {m.Source}" + (m.Confirmed ? "" : " (unconfirmed)"),
          m.CreatedAt,
          m.UpdatedAt))
        .ToListAsync();
      return Results.Ok(rows);
    });

    admin.MapGet("/items", async (MuistioDbContext db, string? q, int page = 0, int size = 0) =>
    {
      page = page <= 0 ? 1 : page;
      size = size <= 0 ? 20 : Math.Clamp(size, 1, 100);
      var query = db.Memories.AsQueryable();
      if (!string.IsNullOrWhiteSpace(q))
      {
        var pattern = $"%{q}%";
        query = query.Where(m => EF.Functions.ILike(m.Content, pattern));
      }
      var total = await query.CountAsync();
      var items = await query
        .OrderByDescending(m => m.UpdatedAt)
        .Skip((page - 1) * size).Take(size)
        .Select(m => new MemoryItem(m.Id, m.Content, m.Category, m.Tags, m.Source, m.Confirmed, m.ExpiresAt, m.CreatedAt, m.UpdatedAt))
        .ToListAsync();
      return Results.Ok(new PagedResult<MemoryItem>(items, page, size, total));
    });

    admin.MapGet("/items/{id:guid}", async (MuistioDbContext db, Guid id) =>
    {
      var m = await db.Memories.FindAsync(id);
      return m is null
        ? Results.NotFound()
        : Results.Ok(new MemoryItem(m.Id, m.Content, m.Category, m.Tags, m.Source, m.Confirmed, m.ExpiresAt, m.CreatedAt, m.UpdatedAt));
    });

    admin.MapPut("/items/{id:guid}", async (HttpContext ctx, MuistioDbContext db, Guid id, MemoryUpdate body) =>
    {
      if (!ctx.Request.Headers.TryGetValue("If-Unmodified-Since", out var ius))
      {
        return Results.StatusCode((int)System.Net.HttpStatusCode.PreconditionRequired);
      }

      if (!DateTimeOffset.TryParse(ius.ToString(), out var clientUpdatedAt))
      {
        return Results.BadRequest(new { error = "invalid-If-Unmodified-Since" });
      }

      var m = await db.Memories.FindAsync(id);
      if (m is null)
      {
        return Results.NotFound();
      }

      if (Math.Abs((m.UpdatedAt - clientUpdatedAt).TotalSeconds) > 1)
      {
        return Results.Conflict(new { error = "stale", currentUpdatedAt = m.UpdatedAt });
      }

      if (body.Content is not null)
      {
        m.Content = body.Content;
      }

      if (body.Category is not null)
      {
        m.Category = body.Category;
      }

      if (body.Tags is not null)
      {
        m.Tags = body.Tags;
      }

      if (body.Confirmed is not null)
      {
        m.Confirmed = body.Confirmed.Value;
      }

      if (body.ExpiresAt is not null)
      {
        m.ExpiresAt = body.ExpiresAt;
      }

      m.UpdatedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync();

      return Results.Ok(new MemoryItem(m.Id, m.Content, m.Category, m.Tags, m.Source, m.Confirmed, m.ExpiresAt, m.CreatedAt, m.UpdatedAt));
    });

    admin.MapDelete("/items/{id:guid}", async (MuistioDbContext db, Guid id) =>
    {
      var m = await db.Memories.FindAsync(id);
      if (m is null)
      {
        return Results.NotFound();
      }

      db.Memories.Remove(m);
      await db.SaveChangesAsync();
      return Results.NoContent();
    });
  }
}
