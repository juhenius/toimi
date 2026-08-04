using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Tools;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class RunTriggerToolTests
{
  private static async Task<(Data.Entity e, Data.Trigger trigger, RunTriggerTool tool, FakeNotifier notifier)> SetupAsync(Data.TietueDbContext db)
  {
    await new TypeRepository(db).DefineAsync("task", /*lang=json,strict*/ """{"type":"object","properties":{"name":{"type":"string"}}}""");
    var entities = new EntityRepository(db, new SchemaValidator());
    var e = await entities.CreateAsync("task", JsonNode.Parse("""{"name":"x"}"""), []);
    var triggers = new TriggerRepository(db, TestConfig.Default);
    var trigger = await triggers.CreateAsync(e.Id, /*lang=json,strict*/ """{"at":"2030-01-01T00:00:00Z"}""", "notify",
      /*lang=json,strict*/ """{"messageTemplate":"ping"}""", DateTimeOffset.UtcNow);
    var notifier = new FakeNotifier();
    var registry = new HandlerRegistry([new NotifyHandler(notifier)]);
    var tool = new RunTriggerTool(db, registry, new EntityEventStore(db));
    return (e, trigger, tool, notifier);
  }

  [Fact]
  public async Task Fires_the_handler_immediately_and_returns_result()
  {
    using var db = TestDb.New();
    var (_, trigger, tool, notifier) = await SetupAsync(db);

    var result = await tool.RunTrigger(trigger.Id.ToString());

    Assert.Single(notifier.Sent);
    Assert.Contains("\"status\"", result);
  }

  [Fact]
  public async Task Does_not_advance_the_schedule()
  {
    using var db = TestDb.New();
    var (_, trigger, tool, _) = await SetupAsync(db);
    var before = trigger.NextFireAt;

    await tool.RunTrigger(trigger.Id.ToString());

    Assert.Equal(before, trigger.NextFireAt);
  }

  [Fact]
  public async Task Records_an_entity_event()
  {
    using var db = TestDb.New();
    var (e, trigger, tool, _) = await SetupAsync(db);

    await tool.RunTrigger(trigger.Id.ToString());

    Assert.Contains(db.EntityEvents, ev => ev.EntityId == e.Id);
  }

  [Fact]
  public async Task Unknown_trigger_returns_message()
  {
    using var db = TestDb.New();
    var (_, _, tool, _) = await SetupAsync(db);

    var result = await tool.RunTrigger(Guid.NewGuid().ToString());

    Assert.Contains("No trigger", result);
  }

  [Fact]
  public async Task Invalid_guid_returns_message()
  {
    using var db = TestDb.New();
    var (_, _, tool, _) = await SetupAsync(db);

    Assert.Contains("Invalid", await tool.RunTrigger("nope"));
  }

  [Fact]
  public async Task Handler_exception_is_reported_not_thrown()
  {
    using var db = TestDb.New();
    var (e, _, _, _) = await SetupAsync(db);
    var triggers = new TriggerRepository(db, TestConfig.Default);
    var bad = await triggers.CreateAsync(e.Id, /*lang=json,strict*/ """{"at":"2030-01-01T00:00:00Z"}""", "script", null, DateTimeOffset.UtcNow);
    var registry = new HandlerRegistry([new ThrowingHandler()]);
    var tool = new RunTriggerTool(db, registry, new EntityEventStore(db));

    var result = await tool.RunTrigger(bad.Id.ToString());

    Assert.Contains("error", result);
  }

  private sealed class ThrowingHandler : INativeHandler
  {
    public string Kind => "script";

    public Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default)
    {
      throw new InvalidOperationException("kaboom");
    }
  }
}
