using toimi.tools.tietue.Data;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TriggerModelTests
{
  [Fact]
  public async Task Trigger_and_event_round_trip()
  {
    using var db = TestDb.New();
    var entityId = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;

    db.Triggers.Add(new Trigger
    {
      Id = Guid.NewGuid(),
      EntityId = entityId,
      Schedule = /*lang=json,strict*/ """{"at":"2026-06-20T09:00:00Z"}""",
      HandlerKind = "notify",
      NextFireAt = now,
      CreatedAt = now,
      UpdatedAt = now,
    });
    db.EntityEvents.Add(new EntityEvent
    {
      Id = Guid.NewGuid(),
      EntityId = entityId,
      OccurrenceUtc = now,
      Kind = "notify",
      Status = "sent",
      CreatedAt = now,
    });
    await db.SaveChangesAsync();

    Assert.Single(db.Triggers);
    Assert.Single(db.EntityEvents);
  }
}
