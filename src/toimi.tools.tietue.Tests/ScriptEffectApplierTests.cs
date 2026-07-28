using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Scripts;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ScriptEffectApplierTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"status":{"type":"string"},"name":{"type":"string"}}}""";

  private static async Task<(Data.Entity entity, EntityRepository entities, FakeNotifier notifier, FakeAgentRunner runner, TriggerRepository triggers, ScriptEffectApplier applier)> SetupAsync(Data.TietueDbContext db)
  {
    await new TypeRepository(db).DefineAsync("task", Schema);
    var entities = new EntityRepository(db, new SchemaValidator());
    var e = await entities.CreateAsync("task", JsonNode.Parse("""{"name":"x","status":"open"}"""), []);
    var notifier = new FakeNotifier();
    var runner = new FakeAgentRunner();
    var triggers = new TriggerRepository(db, TestConfig.Default);
    var handlers = new Lazy<HandlerRegistry>(() => new HandlerRegistry([new NotifyHandler(notifier)]));
    return (e, entities, notifier, runner, triggers, new ScriptEffectApplier(entities, notifier, triggers, runner, handlers, TestConfig.Default));
  }

  [Fact]
  public async Task Applies_setfield_when_granted()
  {
    using var db = TestDb.New();
    var (e, entities, notifier, runner, triggers, applier) = await SetupAsync(db);
    var effects = ScriptEffects.Parse(/*lang=json,strict*/ """{"setField":{"path":"status","value":"done"}}""");

    var applied = await applier.ApplyAsync(e, effects, ["setField"]);

    Assert.Contains("setField", applied);
    var reloaded = await entities.GetAsync(e.Id);
    Assert.Equal("done", reloaded!.Data.RootElement.GetProperty("status").GetString());
  }

  [Fact]
  public async Task Skips_effect_when_capability_not_granted()
  {
    using var db = TestDb.New();
    var (e, entities, notifier, runner, triggers, applier) = await SetupAsync(db);
    var effects = ScriptEffects.Parse(/*lang=json,strict*/ """{"notify":{"message":"hi"}}""");

    var applied = await applier.ApplyAsync(e, effects, []);

    Assert.Empty(applied);
    Assert.Empty(notifier.Sent);
  }

  [Fact]
  public async Task Applies_notify_and_escalate_when_granted()
  {
    using var db = TestDb.New();
    var (e, entities, notifier, runner, triggers, applier) = await SetupAsync(db);
    var effects = ScriptEffects.Parse(/*lang=json,strict*/ """{"notify":{"message":"hi","priority":"high"},"escalate":"think hard"}""");

    var applied = await applier.ApplyAsync(e, effects, ["notify", "escalate"]);

    Assert.Equal("hi", notifier.Sent.Single().Message);
    Assert.Equal("think hard", runner.Runs.Single().Prompt);
    Assert.Contains("notify", applied);
    Assert.Contains("escalate", applied);
  }

  [Fact]
  public async Task Applies_trigger_when_granted()
  {
    using var db = TestDb.New();
    var (e, entities, notifier, runner, triggers, applier) = await SetupAsync(db);
    var effects = ScriptEffects.Parse(/*lang=json,strict*/ """{"trigger":{"schedule":{"at":"2026-07-01T09:00:00Z"},"handlerKind":"notify","handlerConfig":{"titleTemplate":"{name}"}}}""");

    var applied = await applier.ApplyAsync(e, effects, ["trigger"]);

    Assert.Contains("trigger", applied);
    Assert.Single(await triggers.ListByEntityAsync(e.Id));
  }

  [Fact]
  public async Task Does_not_create_trigger_for_unknown_handler_kind()
  {
    using var db = TestDb.New();
    var (e, entities, notifier, runner, triggers, applier) = await SetupAsync(db);
    var effects = ScriptEffects.Parse(/*lang=json,strict*/ """{"trigger":{"schedule":{"at":"2026-07-01T09:00:00Z"},"handlerKind":"bogus"}}""");

    var applied = await applier.ApplyAsync(e, effects, ["trigger"]);

    Assert.DoesNotContain("trigger", applied);
    Assert.Contains(applied, s => s.StartsWith("trigger:error", StringComparison.Ordinal));
    Assert.Empty(await triggers.ListByEntityAsync(e.Id));
  }

  [Fact]
  public async Task Does_not_create_trigger_for_schedule_that_never_fires()
  {
    using var db = TestDb.New();
    var (e, entities, notifier, runner, triggers, applier) = await SetupAsync(db);
    var effects = ScriptEffects.Parse(/*lang=json,strict*/ """{"trigger":{"schedule":{"start":"2020-01-01T00:00:00Z","rrule":"FREQ=YEARLY;COUNT=1"},"handlerKind":"notify"}}""");

    var applied = await applier.ApplyAsync(e, effects, ["trigger"]);

    Assert.DoesNotContain("trigger", applied);
    Assert.Contains(applied, s => s.StartsWith("trigger:error", StringComparison.Ordinal));
    Assert.Empty(await triggers.ListByEntityAsync(e.Id));
  }
}
