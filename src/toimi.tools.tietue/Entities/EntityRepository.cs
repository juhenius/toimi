using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Entities;

public record PagedEntities(IReadOnlyList<Entity> Items, int Page, int Size, int Total);

public class EntityRepository(TietueDbContext db, SchemaValidator validator, IEnumerable<IEntityBehavior>? behaviors = null)
{
  private readonly IReadOnlyList<IEntityBehavior> pipeline = [.. behaviors ?? []];

  public async Task<Entity> CreateAsync(string type, JsonNode? data, string[] tags, CancellationToken ct = default)
  {
    var typeDef = await db.TypeDefinitions.FirstOrDefaultAsync(t => t.Name == type, ct)
      ?? throw new TietueValidationException([$"Unknown type '{type}'. Define it first with define_type."]);
    Validate(typeDef.JsonSchema.RootElement.GetRawText(), data);

    var now = DateTimeOffset.UtcNow;
    var entity = new Entity
    {
      Id = Guid.NewGuid(),
      Type = type,
      Data = JsonSerializer.SerializeToDocument(data),
      Tags = NormalizeTags(tags),
      CreatedAt = now,
      UpdatedAt = now,
    };
    var ctx = new BehaviorContext
    {
      Entity = entity,
      Operation = EntityOperation.Create,
      Behaviors = TypeBehaviors.Parse(typeDef.Behaviors),
      DefaultTriggersJson = typeDef.DefaultTriggers,
      Now = now,
    };

    // Entity, unique-key, and every OnSaving/OnSaved behavior effect (outbox row, default
    // triggers, expiry trigger) must land together: a crash between the entity save and
    // OnSaved would otherwise leave a reminder with no trigger (never fires) or a
    // half-created entity a retry duplicates. Behaviors' own SaveChanges enlist in this
    // ambient transaction (they share this DbContext connection), so they commit or roll
    // back with the entity. InMemory can't begin a transaction, so guard on the relational
    // provider — the call sequence is identical.
    var useTx = db.Database.IsRelational();
    var tx = useTx ? await db.Database.BeginTransactionAsync(ct) : null;
    try
    {
      await EnforceUniqueOnCreateAsync(entity, ctx.Behaviors.UniqueName, ct);
      db.Entities.Add(entity);
      await RunSavingAsync(ctx, ct);

      await SaveGuardingUniqueAsync(entity.Type, ct);
      await RunSavedAsync(ctx, ct);

      if (tx is not null)
      {
        await tx.CommitAsync(ct);
      }
    }
    catch
    {
      if (tx is not null)
      {
        await tx.RollbackAsync(ct);
      }

      throw;
    }
    finally
    {
      if (tx is not null)
      {
        await tx.DisposeAsync();
      }
    }

    // OnCommitted runs AFTER the transaction is committed and disposed: the outbox row is
    // already durable, and a post-commit hiccup (e.g. Qdrant) must not roll back the entity
    // or trigger a rollback-after-commit.
    await RunCommittedAsync(ctx, ct);
    return entity;
  }

  public Task<Entity?> GetAsync(Guid id, CancellationToken ct = default)
  {
    return db.Entities.FirstOrDefaultAsync(e => e.Id == id, ct);
  }

  public async Task<Entity?> UpdateAsync(Guid id, JsonNode? data, string[]? tags, CancellationToken ct = default)
  {
    var entity = await db.Entities.FirstOrDefaultAsync(e => e.Id == id, ct);
    if (entity is null)
    {
      return null;
    }

    TypeDefinition? typeDef = null;
    var parsed = TypeBehaviors.None;
    if (data is not null)
    {
      typeDef = await GetTypeDefOrThrowAsync(entity.Type, ct);
      parsed = TypeBehaviors.Parse(typeDef.Behaviors);
      Validate(typeDef.JsonSchema.RootElement.GetRawText(), data);
      var newData = JsonSerializer.SerializeToDocument(data);
      await EnforceUniqueOnUpdateAsync(entity, newData, parsed.UniqueName, ct);
      // Mutate only after all pre-checks: a caught validation failure inside a scheduler
      // tick must not leave half-applied tracked state for the tick's later saves to flush.
      // The previous JsonDocument is intentionally NOT disposed — the change tracker's
      // original-values snapshot still references it (see ResetPendingChanges).
      entity.Data = newData;
    }

    if (tags is not null)
    {
      entity.Tags = NormalizeTags(tags);
    }

    entity.UpdatedAt = DateTimeOffset.UtcNow;
    var ctx = new BehaviorContext
    {
      Entity = entity,
      Operation = EntityOperation.Update,
      Behaviors = parsed,
      DefaultTriggersJson = typeDef?.DefaultTriggers,
      Now = entity.UpdatedAt,
      DataChanged = data is not null,
    };
    await RunSavingAsync(ctx, ct);

    await SaveGuardingUniqueAsync(entity.Type, ct);
    await RunSavedAsync(ctx, ct);

    await RunCommittedAsync(ctx, ct);
    return entity;
  }

  public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
  {
    var entity = await db.Entities.FirstOrDefaultAsync(e => e.Id == id, ct);
    if (entity is null)
    {
      return false;
    }

    var keys = await db.UniqueKeys.Where(k => k.EntityId == id).ToListAsync(ct);
    db.UniqueKeys.RemoveRange(keys);
    var typeDef = await db.TypeDefinitions.AsNoTracking().FirstOrDefaultAsync(t => t.Name == entity.Type, ct);
    var ctx = new BehaviorContext
    {
      Entity = entity,
      Operation = EntityOperation.Delete,
      Behaviors = TypeBehaviors.Parse(typeDef?.Behaviors),
      DefaultTriggersJson = typeDef?.DefaultTriggers,
      Now = DateTimeOffset.UtcNow,
    };
    db.Entities.Remove(entity);
    await RunSavingAsync(ctx, ct);

    await db.SaveChangesAsync(ct);
    await RunSavedAsync(ctx, ct);

    await RunCommittedAsync(ctx, ct);
    return true;
  }

  private async Task RunSavingAsync(BehaviorContext ctx, CancellationToken ct)
  {
    foreach (var behavior in pipeline)
    {
      await behavior.OnSavingAsync(ctx, ct);
    }
  }

  private async Task RunSavedAsync(BehaviorContext ctx, CancellationToken ct)
  {
    foreach (var behavior in pipeline)
    {
      await behavior.OnSavedAsync(ctx, ct);
    }
  }

  private async Task RunCommittedAsync(BehaviorContext ctx, CancellationToken ct)
  {
    foreach (var behavior in pipeline)
    {
      await behavior.OnCommittedAsync(ctx, ct);
    }
  }

  public async Task<PagedEntities> ListAsync(string? type, string? tag, int page, int size, CancellationToken ct = default)
  {
    page = page <= 0 ? 1 : page;
    size = size <= 0 ? 20 : Math.Clamp(size, 1, 100);

    var query = db.Entities.AsQueryable();
    if (!string.IsNullOrWhiteSpace(type))
    {
      query = query.Where(e => e.Type == type);
    }

    if (!string.IsNullOrWhiteSpace(tag))
    {
      query = query.Where(e => e.Tags.Contains(tag));
    }

    var total = await query.CountAsync(ct);
    var items = await query
      .OrderByDescending(e => e.UpdatedAt)
      .Skip((page - 1) * size)
      .Take(size)
      .ToListAsync(ct);

    return new PagedEntities(items, page, size, total);
  }

  private async Task<TypeDefinition> GetTypeDefOrThrowAsync(string type, CancellationToken ct)
  {
    return await db.TypeDefinitions.FirstOrDefaultAsync(t => t.Name == type, ct)
      ?? throw new TietueValidationException([$"Unknown type '{type}'. Define it first with define_type."]);
  }

  private static string? KeyValue(JsonDocument data, string field)
  {
    return !data.RootElement.TryGetProperty(field, out var v)
      || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
      ? null
      : v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
  }

  private async Task EnforceUniqueOnCreateAsync(Entity entity, UniqueNameConfig? cfg, CancellationToken ct)
  {
    if (cfg is null)
    {
      return;
    }

    var value = KeyValue(entity.Data, cfg.Field);
    if (value is null)
    {
      return;
    }

    if (await db.UniqueKeys.AnyAsync(k => k.Type == entity.Type && k.Field == cfg.Field && k.Value == value, ct))
    {
      throw DuplicateError(entity.Type, cfg.Field, value);
    }

    db.UniqueKeys.Add(new UniqueKey { Type = entity.Type, Field = cfg.Field, Value = value, EntityId = entity.Id });
  }

  private async Task EnforceUniqueOnUpdateAsync(Entity entity, JsonDocument newData, UniqueNameConfig? cfg, CancellationToken ct)
  {
    if (cfg is null)
    {
      return;
    }

    var value = KeyValue(newData, cfg.Field);
    var existing = await db.UniqueKeys.FirstOrDefaultAsync(k => k.EntityId == entity.Id && k.Field == cfg.Field, ct);

    if (value is null)
    {
      if (existing is not null)
      {
        db.UniqueKeys.Remove(existing);
      }

      return;
    }

    if (await db.UniqueKeys.AnyAsync(k => k.Type == entity.Type && k.Field == cfg.Field && k.Value == value && k.EntityId != entity.Id, ct))
    {
      throw DuplicateError(entity.Type, cfg.Field, value);
    }

    if (existing is null)
    {
      db.UniqueKeys.Add(new UniqueKey { Type = entity.Type, Field = cfg.Field, Value = value, EntityId = entity.Id });
    }
    else
    {
      existing.Value = value;
    }
  }

  private async Task SaveGuardingUniqueAsync(string type, CancellationToken ct)
  {
    try
    {
      await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
    {
      ResetPendingChanges();
      throw new TietueValidationException([$"A '{type}' with a duplicate unique field already exists."]);
    }
  }

  // Reverts everything the failed save was about to write, WITHOUT detaching unrelated
  // tracked entities (the scheduler tick's trigger batch shares this scoped context).
  private void ResetPendingChanges()
  {
    foreach (var entry in db.ChangeTracker.Entries()
      .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
      .ToList())
    {
      if (entry.State == EntityState.Added)
      {
        entry.State = EntityState.Detached;
      }
      else if (entry.State == EntityState.Modified)
      {
        entry.CurrentValues.SetValues(entry.OriginalValues);
        entry.State = EntityState.Unchanged;
      }
      else // Deleted
      {
        entry.State = EntityState.Unchanged;
      }
    }
  }

  private static TietueValidationException DuplicateError(string type, string field, string value)
  {
    return new TietueValidationException([$"A '{type}' with {field}='{value}' already exists."]);
  }

  private static string[] NormalizeTags(string[] tags)
  {
    return [.. tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim())];
  }

  private void Validate(string schemaJson, JsonNode? data)
  {
    var result = validator.Validate(schemaJson, data);
    if (!result.IsValid)
    {
      throw new TietueValidationException(result.Errors);
    }
  }
}
