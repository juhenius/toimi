using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using toimi.tools.ruutu.Data.Entities;

namespace toimi.tools.ruutu.Data.Repositories;

public partial class DisplayRepository(RuutuDbContext db)
{
  [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
  private static partial Regex SlugPattern();

  private static bool IsValidIdentifier(string identifier)
  {
    return !string.IsNullOrEmpty(identifier) && SlugPattern().IsMatch(identifier);
  }

  public Task<Display?> GetAsync(string identifier, CancellationToken ct = default)
  {
    return db.Displays.FirstOrDefaultAsync(d => d.Identifier == identifier, ct);
  }

  public Task<List<Display>> ListAsync(CancellationToken ct = default)
  {
    return db.Displays.OrderBy(d => d.Identifier).ToListAsync(ct);
  }

  public async Task<Display> RegisterAsync(string identifier, string? tierOverride, CancellationToken ct = default)
  {
    if (!IsValidIdentifier(identifier))
    {
      throw new ArgumentException(
        $"Invalid display identifier '{identifier}'. Use a lowercase slug: letters, digits, and hyphens, 1-64 chars, not starting with a hyphen.",
        nameof(identifier));
    }

    var existing = await GetAsync(identifier, ct);
    if (existing is not null)
    {
      return existing;
    }

    var display = new Display
    {
      Identifier = identifier,
      Tier = tierOverride,
      TierOverride = tierOverride is not null,
      CreatedAt = DateTimeOffset.UtcNow
    };
    db.Displays.Add(display);
    await db.SaveChangesAsync(ct);
    return display;
  }

  public async Task<bool> UnregisterAsync(string identifier, CancellationToken ct = default)
  {
    var display = await GetAsync(identifier, ct);
    if (display is null)
    {
      return false;
    }

    db.Displays.Remove(display);
    await db.SaveChangesAsync(ct);
    return true;
  }

  public async Task UpdateLastSeenAsync(string identifier, CancellationToken ct = default)
  {
    var d = await GetAsync(identifier, ct);
    if (d is null)
    {
      return;
    }

    d.LastSeenAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
  }

  public async Task RecordCapabilitiesAsync(
    string identifier,
    string? tier,
    string userAgent,
    int viewportWidth,
    int viewportHeight,
    string orientation,
    CancellationToken ct = default)
  {
    var d = await GetAsync(identifier, ct) ?? throw new InvalidOperationException($"Display '{identifier}' not registered");
    if (!d.TierOverride)
    {
      d.Tier = tier;
    }

    d.LastUserAgent = userAgent;
    d.ViewportWidth = viewportWidth;
    d.ViewportHeight = viewportHeight;
    d.Orientation = orientation;
    d.LastSeenAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
  }

  public async Task<bool> SetTierAsync(string identifier, string tier, CancellationToken ct = default)
  {
    var d = await GetAsync(identifier, ct);
    if (d is null)
    {
      return false;
    }

    d.Tier = tier;
    d.TierOverride = true;
    await db.SaveChangesAsync(ct);
    return true;
  }

  public async Task<bool> SetIdleAsync(string identifier, string? template, string? dataJson, CancellationToken ct = default)
  {
    var d = await GetAsync(identifier, ct);
    if (d is null)
    {
      return false;
    }

    d.IdleTemplate = template;
    d.IdleData = dataJson;
    await db.SaveChangesAsync(ct);
    return true;
  }
}
