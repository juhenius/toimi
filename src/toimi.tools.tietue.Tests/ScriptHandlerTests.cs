using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Scripts;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ScriptHandlerTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"status":{"type":"string"},"name":{"type":"string"}}}""";

  private static async Task<(Data.Entity e, FakeNotifier notifier, ScriptHandler handler)> SetupAsync(Data.TietueDbContext db, bool enabled = true)
  {
    await new TypeRepository(db).DefineAsync("task", Schema);
    var entities = new EntityRepository(db, new SchemaValidator());
    var e = await entities.CreateAsync("task", JsonNode.Parse("""{"name":"Jari","status":"open"}"""), []);
    var notifier = new FakeNotifier();
    var applier = new ScriptEffectApplier(entities, notifier, new TriggerRepository(db), new FakeAgentRunner());
    var handler = new ScriptHandler(new ScriptEngine(), applier, new ScriptOptions { Enabled = enabled });
    return (e, notifier, handler);
  }

  [Fact]
  public async Task Runs_script_and_applies_granted_effects()
  {
    using var db = TestDb.New();
    var (e, notifier, handler) = await SetupAsync(db);
    var config = /*lang=json,strict*/ """{"source":"return { notify: { message: 'hello ' + data.name } };","capabilities":["notify"]}""";

    var result = await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));

    Assert.Equal("ran", result.Status);
    Assert.Equal("hello Jari", notifier.Sent.Single().Message);
    Assert.Contains("notify", result.Result);
  }

  [Fact]
  public async Task Does_not_apply_ungranted_effects()
  {
    using var db = TestDb.New();
    var (e, notifier, handler) = await SetupAsync(db);
    var config = /*lang=json,strict*/ """{"source":"return { notify: { message: 'x' } };","capabilities":[]}""";

    await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));

    Assert.Empty(notifier.Sent);
  }

  [Fact]
  public async Task Disabled_kill_switch_skips_execution()
  {
    using var db = TestDb.New();
    var (e, notifier, handler) = await SetupAsync(db, enabled: false);
    var config = /*lang=json,strict*/ """{"source":"return { notify: { message: 'x' } };","capabilities":["notify"]}""";

    var result = await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));

    Assert.Equal("disabled", result.Status);
    Assert.Empty(notifier.Sent);
  }
}
