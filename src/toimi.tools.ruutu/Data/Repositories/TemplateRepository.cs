using Microsoft.EntityFrameworkCore;
using toimi.tools.ruutu.Data.Entities;

namespace toimi.tools.ruutu.Data.Repositories;

public class TemplateRepository(RuutuDbContext db)
{
  public Task<Template?> GetAsync(string name, CancellationToken ct = default)
  {
    return db.Templates.FirstOrDefaultAsync(t => t.Name == name, ct);
  }

  public Task<List<Template>> ListAsync(CancellationToken ct = default)
  {
    return db.Templates.OrderBy(t => t.Name).ToListAsync(ct);
  }

  public async Task UpsertSeededAsync(
    string name, string description, string schemaJson,
    string modernHtml, string legacyHtml,
    CancellationToken ct = default)
  {
    var existing = await GetAsync(name, ct);
    var now = DateTimeOffset.UtcNow;
    if (existing is null)
    {
      db.Templates.Add(new Template
      {
        Name = name,
        Description = description,
        SchemaJson = schemaJson,
        ModernHtml = modernHtml,
        LegacyHtml = legacyHtml,
        IsSeeded = true,
        CreatedAt = now,
        UpdatedAt = now
      });
    }
    else
    {
      existing.Description = description;
      existing.SchemaJson = schemaJson;
      existing.ModernHtml = modernHtml;
      existing.LegacyHtml = legacyHtml;
      existing.IsSeeded = true;
      existing.UpdatedAt = now;
    }
    await db.SaveChangesAsync(ct);
  }

  public async Task<Template> UpsertAiAsync(
    string name, string description, string schemaJson,
    string? modernHtml, string? legacyHtml,
    CancellationToken ct = default)
  {
    var existing = await GetAsync(name, ct);
    var now = DateTimeOffset.UtcNow;
    if (existing is null)
    {
      var t = new Template
      {
        Name = name,
        Description = description,
        SchemaJson = schemaJson,
        ModernHtml = modernHtml,
        LegacyHtml = legacyHtml,
        IsSeeded = false,
        CreatedAt = now,
        UpdatedAt = now
      };
      db.Templates.Add(t);
      await db.SaveChangesAsync(ct);
      return t;
    }
    if (existing.IsSeeded)
    {
      throw new InvalidOperationException($"Cannot modify seeded template '{name}'");
    }

    existing.Description = description;
    existing.SchemaJson = schemaJson;
    if (modernHtml is not null)
    {
      existing.ModernHtml = modernHtml;
    }

    if (legacyHtml is not null)
    {
      existing.LegacyHtml = legacyHtml;
    }

    existing.UpdatedAt = now;
    await db.SaveChangesAsync(ct);
    return existing;
  }

  public async Task<bool> DeleteAsync(string name, CancellationToken ct = default)
  {
    var t = await GetAsync(name, ct);
    if (t is null)
    {
      return false;
    }

    if (t.IsSeeded)
    {
      throw new InvalidOperationException($"Cannot delete seeded template '{name}'");
    }

    db.Templates.Remove(t);
    await db.SaveChangesAsync(ct);
    return true;
  }
}
