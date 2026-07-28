using Microsoft.EntityFrameworkCore;
using Toimi.Core.Admin;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Semantic;

namespace toimi.tools.tietue.Admin;

public static class AdminEndpoints
{
  public record EntityItem(
      Guid Id, string Type, string Data, string[] Tags,
      DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

  public record TypeItem(
      string Name, string JsonSchema, string? Behaviors, string? DefaultTriggers,
      DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

  public record TriggerItem(
      Guid Id, string Schedule, string HandlerKind, string? HandlerConfig,
      bool Enabled, DateTimeOffset? NextFireAt, DateTimeOffset? LastFiredAt);

  public record EventItem(
      Guid Id, DateTimeOffset OccurrenceUtc, string Kind, string Status, string? Result, DateTimeOffset CreatedAt);

  public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Size, int Total);

  public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
  {
    var admin = app.MapAdmin();

    admin.MapGet("/summary", async (TietueDbContext db, string? q, int limit = 0) =>
    {
      limit = limit <= 0 ? 50 : Math.Clamp(limit, 1, 200);
      var query = db.Entities.AsQueryable();
      if (!string.IsNullOrWhiteSpace(q))
      {
        query = query.Where(e => e.Type == q);
      }

      var rows = await query
        .OrderByDescending(e => e.UpdatedAt)
        .Take(limit)
        .Select(e => new AdminSummaryDto(
          e.Id.ToString(),
          e.Type,
          e.Type,
          $"{e.Tags.Length} tag(s)",
          e.CreatedAt,
          e.UpdatedAt))
        .ToListAsync();
      return Results.Ok(rows);
    });

    admin.MapGet("/items", async (TietueDbContext db, string? q, int page = 0, int size = 0) =>
    {
      page = page <= 0 ? 1 : page;
      size = size <= 0 ? 20 : Math.Clamp(size, 1, 100);
      var query = db.Entities.AsQueryable();
      if (!string.IsNullOrWhiteSpace(q))
      {
        query = query.Where(e => e.Type == q);
      }

      var total = await query.CountAsync();
      var rows = await query
        .OrderByDescending(e => e.UpdatedAt)
        .Skip((page - 1) * size).Take(size)
        .ToListAsync();
      var items = rows
        .Select(e => new EntityItem(e.Id, e.Type, e.Data.RootElement.GetRawText(), e.Tags, e.CreatedAt, e.UpdatedAt))
        .ToList();
      return Results.Ok(new PagedResult<EntityItem>(items, page, size, total));
    });

    admin.MapGet("/items/{id:guid}", async (TietueDbContext db, Guid id) =>
    {
      var e = await db.Entities.FindAsync(id);
      return e is null
        ? Results.NotFound()
        : Results.Ok(new EntityItem(e.Id, e.Type, e.Data.RootElement.GetRawText(), e.Tags, e.CreatedAt, e.UpdatedAt));
    });

    admin.MapDelete("/items/{id:guid}", async (Entities.EntityRepository repo, Guid id) =>
    {
      var deleted = await repo.DeleteAsync(id);
      return deleted ? Results.NoContent() : Results.NotFound();
    });

    admin.MapGet("/items/{id:guid}/triggers", async (TietueDbContext db, Guid id) =>
    {
      var rows = await db.Triggers.Where(t => t.EntityId == id).OrderBy(t => t.CreatedAt)
        .Select(t => new TriggerItem(t.Id, t.Schedule, t.HandlerKind, t.HandlerConfig, t.Enabled, t.NextFireAt, t.LastFiredAt))
        .ToListAsync();
      return Results.Ok(rows);
    });

    admin.MapGet("/items/{id:guid}/events", async (TietueDbContext db, Guid id, int limit = 0) =>
    {
      limit = limit <= 0 ? 50 : Math.Clamp(limit, 1, 200);
      var rows = await db.EntityEvents.Where(e => e.EntityId == id).OrderByDescending(e => e.CreatedAt).Take(limit)
        .Select(e => new EventItem(e.Id, e.OccurrenceUtc, e.Kind, e.Status, e.Result, e.CreatedAt))
        .ToListAsync();
      return Results.Ok(rows);
    });

    admin.MapGet("/types", async (TietueDbContext db) =>
    {
      var defs = await db.TypeDefinitions.OrderBy(t => t.Name).ToListAsync();
      var rows = defs
        .Select(t => new TypeItem(t.Name, t.JsonSchema.RootElement.GetRawText(), t.Behaviors, t.DefaultTriggers, t.CreatedAt, t.UpdatedAt))
        .ToList();
      return Results.Ok(rows);
    });

    admin.MapGet("/types/{name}", async (TietueDbContext db, string name) =>
    {
      var t = await db.TypeDefinitions.FirstOrDefaultAsync(x => x.Name == name);
      return t is null
        ? Results.NotFound()
        : Results.Ok(new TypeItem(t.Name, t.JsonSchema.RootElement.GetRawText(), t.Behaviors, t.DefaultTriggers, t.CreatedAt, t.UpdatedAt));
    });

    admin.MapGet("/outbox", async (TietueDbContext db) =>
    {
      var rows = await db.IndexOutbox.OrderBy(o => o.CreatedAt).ToListAsync();
      return Results.Ok(new
      {
        pending = rows.Count(r => r.Attempts == 0),
        failing = rows.Count(r => r.Attempts is > 0 and < SemanticOutbox.MaxAttempts),
        dead = rows.Count(r => r.Attempts >= SemanticOutbox.MaxAttempts),
        deadRows = rows.Where(r => r.Attempts >= SemanticOutbox.MaxAttempts)
          .Select(r => new { r.Id, r.EntityId, r.Type, r.Op, r.Attempts, r.LastError, r.LastAttemptAt })
          .ToList(),
      });
    });

    admin.MapPost("/semantic/reconcile/{type}", async (TietueDbContext db, ISemanticIndex index, string type) =>
    {
      try
      {
        var result = await SemanticReconciler.ReconcileAsync(db, index, type, CancellationToken.None);
        return Results.Ok(result);
      }
      catch (InvalidOperationException ex)
      {
        return Results.BadRequest(new { error = ex.Message });
      }
    });
  }
}
