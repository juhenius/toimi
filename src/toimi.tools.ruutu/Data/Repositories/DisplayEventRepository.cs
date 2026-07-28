using Microsoft.EntityFrameworkCore;
using toimi.tools.ruutu.Data.Entities;

namespace toimi.tools.ruutu.Data.Repositories;

public class DisplayEventRepository(RuutuDbContext db)
{
  public async Task<DisplayEvent> AppendAsync(
    int displayId, string eventType, string? target, string? valueJson,
    CancellationToken ct = default)
  {
    var e = new DisplayEvent
    {
      DisplayId = displayId,
      EventType = eventType,
      Target = target,
      Value = valueJson,
      CreatedAt = DateTimeOffset.UtcNow
    };
    db.DisplayEvents.Add(e);
    await db.SaveChangesAsync(ct);
    return e;
  }

  public Task<List<DisplayEvent>> GetSinceAsync(int displayId, DateTimeOffset? since, CancellationToken ct = default)
  {
    var q = db.DisplayEvents.Where(e => e.DisplayId == displayId);
    if (since.HasValue)
    {
      q = q.Where(e => e.CreatedAt > since.Value);
    }

    return q.OrderByDescending(e => e.CreatedAt).Take(200).ToListAsync(ct);
  }
}
