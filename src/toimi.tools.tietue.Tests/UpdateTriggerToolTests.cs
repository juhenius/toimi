using System.Text.Json;
using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Tools;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class UpdateTriggerToolTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""";
  private static readonly DateTimeOffset Past = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

  private static async Task<(Data.TietueDbContext db, TriggerRepository triggers, UpdateTriggerTool tool, Guid entityId)> SetupAsync()
  {
    var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("reminder", Schema);
    var repo = new EntityRepository(db, new SchemaValidator());
    var e = await repo.CreateAsync("reminder", JsonNode.Parse("""{"title":"Call"}"""), []);
    var triggers = new TriggerRepository(db, TestConfig.Default);
    var tool = new UpdateTriggerTool(triggers);
    return (db, triggers, tool, e.Id);
  }

  [Fact]
  public async Task Invalid_guid_is_rejected()
  {
    var (db, _, tool, _) = await SetupAsync();
    using var _1 = db;
    Assert.Equal("Invalid id. Expected a GUID.", await tool.UpdateTrigger("not-a-guid"));
  }

  [Fact]
  public async Task Unknown_id_reports_not_found()
  {
    var (db, _, tool, _) = await SetupAsync();
    using var _1 = db;
    var id = Guid.NewGuid().ToString();
    Assert.Equal($"Trigger '{id}' not found.", await tool.UpdateTrigger(id));
  }

  [Fact]
  public async Task Handler_config_update_leaves_schedule_and_next_fire_untouched()
  {
    var (db, triggers, tool, entityId) = await SetupAsync();
    using var _1 = db;
    var t = await triggers.CreateAsync(entityId, /*lang=json,strict*/ """{"at":"2027-01-01T09:00:00Z"}""", "notify", null, Past);
    var before = t.NextFireAt;

    await tool.UpdateTrigger(t.Id.ToString(), handlerConfig: /*lang=json,strict*/ """{"titleTemplate":"new"}""");

    var updated = (await triggers.ListByEntityAsync(entityId))[0];
    Assert.Equal(/*lang=json,strict*/ """{"titleTemplate":"new"}""", updated.HandlerConfig);
    Assert.Equal(before, updated.NextFireAt);
  }

  [Fact]
  public async Task Reenabling_an_exhausted_trigger_never_yields_enabled_with_null_next_fire()
  {
    var (db, triggers, tool, entityId) = await SetupAsync();
    using var _1 = db;
    var t = await triggers.CreateAsync(entityId, /*lang=json,strict*/ """{"at":"2026-01-02T09:00:00Z"}""", "notify", null, Past);
    // Simulate scheduler exhaustion: one-shot fired, disabled, no next fire.
    t.Enabled = false;
    t.NextFireAt = null;
    await db.SaveChangesAsync();

    var response = await tool.UpdateTrigger(t.Id.ToString(), enabled: true);

    // The invariant under test: never Enabled=true with NextFireAt=null (a
    // permanently dead-but-enabled trigger, invisible to the scheduler).
    var updated = (await triggers.ListByEntityAsync(entityId))[0];
    Assert.False(updated.Enabled && updated.NextFireAt is null);
    // For a one-shot whose 'at' is in the past there is nothing to recompute:
    // the trigger must stay disabled and the tool response must say so.
    Assert.False(updated.Enabled);
    using var doc = JsonDocument.Parse(response);
    Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
  }

  [Fact]
  public async Task Reenabling_with_a_still_live_recurring_schedule_recomputes_next_fire()
  {
    var (db, triggers, tool, entityId) = await SetupAsync();
    using var _1 = db;
    var t = await triggers.CreateAsync(
      entityId, /*lang=json,strict*/ """{"start":"2026-01-01T09:00:00Z","rrule":"FREQ=DAILY","tz":"UTC"}""", "notify", null, Past);
    t.Enabled = false;
    t.NextFireAt = null;
    await db.SaveChangesAsync();

    await tool.UpdateTrigger(t.Id.ToString(), enabled: true);

    var updated = (await triggers.ListByEntityAsync(entityId))[0];
    Assert.True(updated.Enabled);
    Assert.NotNull(updated.NextFireAt);
  }

  [Fact]
  public async Task Reenabling_a_paused_trigger_keeps_its_next_fire()
  {
    var (db, triggers, tool, entityId) = await SetupAsync();
    using var _1 = db;
    var t = await triggers.CreateAsync(entityId, /*lang=json,strict*/ """{"at":"2027-06-01T09:00:00Z"}""", "notify", null, Past);
    var scheduled = t.NextFireAt;
    t.Enabled = false;
    await db.SaveChangesAsync();

    await tool.UpdateTrigger(t.Id.ToString(), enabled: true);

    var updated = (await triggers.ListByEntityAsync(entityId))[0];
    Assert.True(updated.Enabled);
    Assert.Equal(scheduled, updated.NextFireAt);
  }
}
