using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Types;

public class TypeRepository(TietueDbContext db)
{
  public async Task<TypeDefinition> DefineAsync(string name, string schemaJson, string? behaviorsJson = null, string? defaultTriggersJson = null, CancellationToken ct = default)
  {
    JsonDocument schema;
    try
    {
      schema = JsonDocument.Parse(schemaJson);
    }
    catch (JsonException ex)
    {
      throw new TietueValidationException([$"Invalid schema JSON: {ex.Message}"]);
    }

    if (behaviorsJson is not null)
    {
      try
      {
        using var _ = JsonDocument.Parse(behaviorsJson);
      }
      catch (JsonException ex)
      {
        throw new TietueValidationException([$"Invalid behaviors JSON: {ex.Message}"]);
      }
    }

    if (defaultTriggersJson is not null)
    {
      try
      {
        using var _ = JsonDocument.Parse(defaultTriggersJson);
      }
      catch (JsonException ex)
      {
        throw new TietueValidationException([$"Invalid default triggers JSON: {ex.Message}"]);
      }
    }

    var now = DateTimeOffset.UtcNow;
    var existing = await db.TypeDefinitions.FirstOrDefaultAsync(t => t.Name == name, ct);
    if (existing is null)
    {
      existing = new TypeDefinition { Name = name, JsonSchema = schema, Behaviors = behaviorsJson, DefaultTriggers = defaultTriggersJson, CreatedAt = now, UpdatedAt = now };
      db.TypeDefinitions.Add(existing);
    }
    else
    {
      existing.JsonSchema = schema;
      existing.Behaviors = behaviorsJson;
      existing.DefaultTriggers = defaultTriggersJson;
      existing.UpdatedAt = now;
    }

    await db.SaveChangesAsync(ct);
    return existing;
  }

  public Task<TypeDefinition?> GetAsync(string name, CancellationToken ct = default)
  {
    return db.TypeDefinitions.FirstOrDefaultAsync(t => t.Name == name, ct);
  }

  public async Task<IReadOnlyList<TypeDefinition>> ListAsync(CancellationToken ct = default)
  {
    return await db.TypeDefinitions.OrderBy(t => t.Name).ToListAsync(ct);
  }

  public async Task<bool> DeleteAsync(string name, CancellationToken ct = default)
  {
    var t = await db.TypeDefinitions.FirstOrDefaultAsync(x => x.Name == name, ct);
    if (t is null)
    {
      return false;
    }

    db.TypeDefinitions.Remove(t);
    await db.SaveChangesAsync(ct);
    return true;
  }
}
