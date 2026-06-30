using toimi.tools.tietue.Data;
using toimi.tools.tietue.Provisioning;
using toimi.tools.tietue.Scheduling;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TriggerProvisionerTests
{
  private const string DefaultTriggers = /*lang=json,strict*/ """
  [{"when":{"atField":"dueAt","rruleField":"rrule","tzField":"timezone"},
    "handler":{"kind":"notify","config":{"titleTemplate":"{title}"}}}]
  """;

  private static Entity Reminder(string dataJson)
  {
    return new()
    {
      Id = Guid.NewGuid(),
      Type = "reminder",
      Data = System.Text.Json.JsonDocument.Parse(dataJson),
      CreatedAt = DateTimeOffset.UtcNow,
      UpdatedAt = DateTimeOffset.UtcNow,
    };
  }

  [Fact]
  public async Task Provisions_one_shot_trigger_from_due_field()
  {
    using var db = TestDb.New();
    var provisioner = new TriggerProvisioner(new TriggerRepository(db));
    var e = Reminder(/*lang=json,strict*/ """{"title":"Call","dueAt":"2026-06-20T09:00:00Z"}""");

    await provisioner.ProvisionAsync(e, DefaultTriggers, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    var triggers = await new TriggerRepository(db).ListByEntityAsync(e.Id);
    var t = Assert.Single(triggers);
    Assert.Equal("notify", t.HandlerKind);
    Assert.Equal(new DateTimeOffset(2026, 6, 20, 9, 0, 0, TimeSpan.Zero), t.NextFireAt);
  }

  [Fact]
  public async Task Provisions_recurring_trigger_when_rrule_present()
  {
    using var db = TestDb.New();
    var provisioner = new TriggerProvisioner(new TriggerRepository(db));
    var e = Reminder(/*lang=json,strict*/ """{"title":"Standup","dueAt":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""");

    await provisioner.ProvisionAsync(e, DefaultTriggers, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    var t = Assert.Single(await new TriggerRepository(db).ListByEntityAsync(e.Id));
    Assert.Contains("FREQ=DAILY", t.Schedule);
  }

  [Fact]
  public async Task No_triggers_when_definition_is_null()
  {
    using var db = TestDb.New();
    var provisioner = new TriggerProvisioner(new TriggerRepository(db));
    var e = Reminder(/*lang=json,strict*/ """{"title":"x"}""");

    await provisioner.ProvisionAsync(e, null, DateTimeOffset.UtcNow);

    Assert.Empty(await new TriggerRepository(db).ListByEntityAsync(e.Id));
  }
}
