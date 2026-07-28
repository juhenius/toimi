using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ClaimCollisionTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""";
  private static readonly DateTimeOffset Occurrence = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

  [Fact]
  public async Task Collision_detaches_only_the_claim_and_keeps_other_tracked_entities_saveable()
  {
    var options = new DbContextOptionsBuilder<TietueDbContext>()
      .UseInMemoryDatabase($"tietue-{Guid.NewGuid()}")
      .Options;
    using var db = new ThrowOnceDbContext(options);
    await new TypeRepository(db).DefineAsync("reminder", Schema);
    var e = await new EntityRepository(db, new SchemaValidator()).CreateAsync(
      "reminder", JsonNode.Parse("""{"title":"Call"}"""), []);
    await new TriggerRepository(db, TestConfig.Default).CreateAsync(
      e.Id, /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify",
      /*lang=json,strict*/ """{"titleTemplate":"{title}"}""",
      new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero));

    // Hold the trigger TRACKED, like SchedulerTick's due batch during a tick.
    var trigger = await db.Triggers.FirstAsync();

    db.ThrowNext = true;
    var result = await new EntityEventStore(db).TryClaimAsync(e.Id, Occurrence, "notify", DateTimeOffset.UtcNow);
    Assert.Equal(ClaimResult.InProgress, result);

    // The rest of the batch must still persist: advance the tracked trigger.
    trigger.Enabled = false;
    await db.SaveChangesAsync();

    using var fresh = new TietueDbContext(options);
    var reloaded = await fresh.Triggers.SingleAsync();
    Assert.False(reloaded.Enabled); // the tracked mutation survived the failed claim
    Assert.False(await fresh.EntityEvents.AnyAsync(ev => ev.Status == "started")); // failed claim not persisted or re-added
  }
}
