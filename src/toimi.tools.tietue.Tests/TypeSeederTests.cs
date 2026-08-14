using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Seed;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TypeSeederTests
{
  [Fact]
  public async Task Seeds_memory_and_skill_types_with_semantic_index()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    await new TypeSeeder(repo).SeedAsync();

    var memory = await repo.GetAsync("memory");
    var skill = await repo.GetAsync("skill");
    Assert.NotNull(memory);
    Assert.NotNull(skill);
    Assert.Contains("SemanticIndex", memory.Behaviors);
    Assert.Contains("Expiry", memory.Behaviors);
    Assert.Contains("SemanticIndex", skill.Behaviors);
    Assert.Contains("UniqueName", skill.Behaviors);
  }

  [Fact]
  public async Task Seeding_twice_is_idempotent()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    await new TypeSeeder(repo).SeedAsync();
    await new TypeSeeder(repo).SeedAsync();

    Assert.Equal(5, (await repo.ListAsync()).Count);
  }

  [Fact]
  public async Task Seeds_reminder_with_default_notify_trigger()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    await new TypeSeeder(repo).SeedAsync();

    var reminder = await repo.GetAsync("reminder");
    Assert.NotNull(reminder);
    Assert.Contains("notify", reminder.DefaultTriggers);
    Assert.Contains("dueAt", reminder.DefaultTriggers);
  }

  [Fact]
  public async Task Seeds_job_type_with_unique_name_and_script_trigger()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    await new TypeSeeder(repo).SeedAsync();

    var job = await repo.GetAsync("job");
    Assert.NotNull(job);
    Assert.Contains("UniqueName", job.Behaviors);
    Assert.Contains("\"kind\":\"script\"", job.DefaultTriggers);
    Assert.Contains("fromEntity", job.DefaultTriggers);
    var schema = job.JsonSchema.RootElement.GetRawText();
    Assert.Contains("startAt", schema);
    Assert.Contains("allowedHosts", schema);
    Assert.Contains("grants", schema);
  }

  [Fact]
  public async Task Job_with_rrule_but_no_startAt_is_rejected_loudly()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);
    await new TypeSeeder(repo).SeedAsync();
    var entities = new EntityRepository(db, new SchemaValidator());

    // Without the dependentRequired guard this saved fine and sat silently inert:
    // TriggerProvisioner bails on the missing startAt and never reads the rrule.
    await Assert.ThrowsAsync<TietueValidationException>(() =>
      entities.CreateAsync("job", System.Text.Json.Nodes.JsonNode.Parse(
        /*lang=json,strict*/ """{"name":"hourly","code":"export default async function(){return {};}","rrule":"FREQ=HOURLY"}"""), []));
  }

  [Fact]
  public async Task Seeds_schedule_with_default_message_trigger()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    await new TypeSeeder(repo).SeedAsync();

    var schedule = await repo.GetAsync("schedule");
    Assert.NotNull(schedule);
    Assert.Contains("message", schedule.DefaultTriggers);
    Assert.Contains("prompt", schedule.DefaultTriggers);
  }

  [Fact]
  public async Task Seeded_types_pass_full_default_trigger_validation()
  {
    using var db = TestDb.New();
    var entities = new EntityRepository(db, new SchemaValidator());
    var registry = new Handlers.HandlerRegistry(
    [
      new Handlers.NotifyHandler(new FakeNotifier()),
      new Handlers.MessageHandler(new FakeAgentRunner()),
      new Handlers.SetFieldHandler(entities),
      new Handlers.DeleteHandler(entities),
      new Handlers.ScriptHandler(new FakeSuoritinClient(),
        new Scripts.ScriptEffectApplier(entities, new FakeMcpInvoker()),
        new Scripts.RunTokenStore(), new Scripts.ScriptOptions(), new Scripts.SuoritinOptions()),
    ]);
    var repo = new TypeRepository(db, registry);

    // Must not throw: reminder's notify, schedule's message, and job's fromEntity script
    // configs are the reference examples of valid DefaultTriggers.
    await new TypeSeeder(repo).SeedAsync();

    Assert.Equal(5, (await repo.ListAsync()).Count);
  }
}
