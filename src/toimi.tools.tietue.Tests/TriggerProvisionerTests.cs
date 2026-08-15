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
    var provisioner = new TriggerProvisioner(new TriggerRepository(db, TestConfig.Default));
    var e = Reminder(/*lang=json,strict*/ """{"title":"Call","dueAt":"2026-06-20T09:00:00Z"}""");

    await provisioner.ProvisionAsync(e, DefaultTriggers, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    var triggers = await new TriggerRepository(db, TestConfig.Default).ListByEntityAsync(e.Id);
    var t = Assert.Single(triggers);
    Assert.Equal("notify", t.HandlerKind);
    Assert.Equal(new DateTimeOffset(2026, 6, 20, 9, 0, 0, TimeSpan.Zero), t.NextFireAt);
  }

  [Fact]
  public async Task Provisions_recurring_trigger_when_rrule_present()
  {
    using var db = TestDb.New();
    var provisioner = new TriggerProvisioner(new TriggerRepository(db, TestConfig.Default));
    var e = Reminder(/*lang=json,strict*/ """{"title":"Standup","dueAt":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""");

    await provisioner.ProvisionAsync(e, DefaultTriggers, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    var t = Assert.Single(await new TriggerRepository(db, TestConfig.Default).ListByEntityAsync(e.Id));
    Assert.Contains("FREQ=DAILY", t.Schedule);
  }

  [Fact]
  public async Task No_triggers_when_definition_is_null()
  {
    using var db = TestDb.New();
    var provisioner = new TriggerProvisioner(new TriggerRepository(db, TestConfig.Default));
    var e = Reminder(/*lang=json,strict*/ """{"title":"x"}""");

    await provisioner.ProvisionAsync(e, null, DateTimeOffset.UtcNow);

    Assert.Empty(await new TriggerRepository(db, TestConfig.Default).ListByEntityAsync(e.Id));
  }

  [Fact]
  public async Task Garbage_due_date_provisions_no_trigger()
  {
    using var db = TestDb.New();
    var provisioner = new TriggerProvisioner(new TriggerRepository(db, TestConfig.Default));
    var e = Reminder(/*lang=json,strict*/ """{"title":"x","dueAt":"whenever"}""");

    await provisioner.ProvisionAsync(e, DefaultTriggers, DateTimeOffset.UtcNow);

    Assert.Empty(await new TriggerRepository(db, TestConfig.Default).ListByEntityAsync(e.Id));
  }

  [Fact]
  public async Task Exhausted_recurrence_from_entity_data_is_skipped_not_thrown()
  {
    using var db = TestDb.New();
    var provisioner = new TriggerProvisioner(new TriggerRepository(db, TestConfig.Default));
    var e = Reminder(/*lang=json,strict*/ """{"title":"Old","dueAt":"2020-01-01T09:00:00Z","rrule":"FREQ=DAILY;COUNT=1"}""");

    // The provision (running inside entity create in prod) must swallow the repository's
    // rejection: the entity survives, the dead template is skipped.
    await provisioner.ProvisionAsync(e, DefaultTriggers, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    Assert.Empty(await new TriggerRepository(db, TestConfig.Default).ListByEntityAsync(e.Id));
  }
  [Fact]
  public async Task Provisions_webhook_trigger_unconditionally_with_secret()
  {
    using var db = TestDb.New();
    var provisioner = new TriggerProvisioner(new TriggerRepository(db, TestConfig.Default));
    var e = Reminder(/*lang=json,strict*/ """{"title":"Ring"}""");
    const string templates = /*lang=json,strict*/ """
    [{"when":{"webhook":{"rateLimit":3}},
      "handler":{"kind":"notify","config":{"titleTemplate":"{title}"}}}]
    """;

    await provisioner.ProvisionAsync(e, templates, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    var t = Assert.Single(await new TriggerRepository(db, TestConfig.Default).ListByEntityAsync(e.Id));
    Assert.True(t.Enabled);
    Assert.Null(t.NextFireAt);
    Assert.NotNull(t.Secret);
    Assert.Contains("\"rateLimit\":3", t.Schedule);
  }

  private const string ScheduleTriggers = /*lang=json,strict*/ """
  [{"when":{"atField":"startAt"},
    "handler":{"kind":"message","config":{"promptTemplate":"{prompt}","modelField":"model"}}}]
  """;

  [Fact]
  public async Task ModelField_copies_the_entity_model_pin_into_the_handler_config()
  {
    using var db = TestDb.New();
    var provisioner = new TriggerProvisioner(new TriggerRepository(db, TestConfig.Default));
    var e = Reminder(/*lang=json,strict*/ """{"prompt":"analyze","startAt":"2026-06-20T09:00:00Z","model":"smart"}""");

    await provisioner.ProvisionAsync(e, ScheduleTriggers, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    var t = Assert.Single(await new TriggerRepository(db, TestConfig.Default).ListByEntityAsync(e.Id));
    Assert.Contains("\"model\":\"smart\"", t.HandlerConfig);
    Assert.DoesNotContain("modelField", t.HandlerConfig);
  }

  [Fact]
  public async Task Absent_model_field_drops_the_key_and_leaves_the_fast_default()
  {
    using var db = TestDb.New();
    var provisioner = new TriggerProvisioner(new TriggerRepository(db, TestConfig.Default));
    var e = Reminder(/*lang=json,strict*/ """{"prompt":"analyze","startAt":"2026-06-20T09:00:00Z"}""");

    await provisioner.ProvisionAsync(e, ScheduleTriggers, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    var t = Assert.Single(await new TriggerRepository(db, TestConfig.Default).ListByEntityAsync(e.Id));
    Assert.DoesNotContain("model", t.HandlerConfig);
  }
}
