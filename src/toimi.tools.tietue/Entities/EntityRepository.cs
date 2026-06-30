using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Provisioning;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Entities;

public record PagedEntities(IReadOnlyList<Entity> Items, int Page, int Size, int Total);

public class EntityRepository(TietueDbContext db, SchemaValidator validator, BehaviorDispatcher? dispatcher = null, TriggerProvisioner? provisioner = null, ExpiryReconciler? expiry = null)
{
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
    db.Entities.Add(entity);
    await EnforceUniqueOnCreateAsync(entity, typeDef.Behaviors, ct);
    await SaveGuardingUniqueAsync(entity.Type, ct);
    if (dispatcher is not null)
    {
      await dispatcher.OnEntitySavedAsync(entity, ct);
    }

    if (provisioner is not null)
    {
      await provisioner.ProvisionAsync(entity, typeDef.DefaultTriggers, entity.CreatedAt, ct);
    }

    if (expiry is not null)
    {
      await expiry.ReconcileAsync(entity, typeDef.Behaviors, entity.CreatedAt, ct);
    }

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

    string? behaviorsForExpiry = null;
    if (data is not null)
    {
      var typeDef = await GetTypeDefOrThrowAsync(entity.Type, ct);
      Validate(typeDef.JsonSchema.RootElement.GetRawText(), data);
      var previous = entity.Data;
      entity.Data = JsonSerializer.SerializeToDocument(data);
      previous.Dispose();
      await EnforceUniqueOnUpdateAsync(entity, typeDef.Behaviors, ct);
      behaviorsForExpiry = typeDef.Behaviors;
    }

    if (tags is not null)
    {
      entity.Tags = NormalizeTags(tags);
    }

    entity.UpdatedAt = DateTimeOffset.UtcNow;
    await SaveGuardingUniqueAsync(entity.Type, ct);
    if (expiry is not null && data is not null)
    {
      await expiry.ReconcileAsync(entity, behaviorsForExpiry, entity.UpdatedAt, ct);
    }

    if (dispatcher is not null)
    {
      await dispatcher.OnEntitySavedAsync(entity, ct);
    }

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
    db.Entities.Remove(entity);
    await db.SaveChangesAsync(ct);
    if (dispatcher is not null)
    {
      await dispatcher.OnEntityDeletedAsync(entity, ct);
    }

    return true;
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

  private async Task EnforceUniqueOnCreateAsync(Entity entity, string? behaviorsJson, CancellationToken ct)
  {
    var cfg = BehaviorSpec.UniqueNameOf(behaviorsJson);
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

  private async Task EnforceUniqueOnUpdateAsync(Entity entity, string? behaviorsJson, CancellationToken ct)
  {
    var cfg = BehaviorSpec.UniqueNameOf(behaviorsJson);
    if (cfg is null)
    {
      return;
    }

    var value = KeyValue(entity.Data, cfg.Field);
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
      throw new TietueValidationException([$"A '{type}' with a duplicate unique field already exists."]);
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
