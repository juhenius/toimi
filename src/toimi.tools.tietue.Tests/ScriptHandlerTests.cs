using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Scripts;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

// Shares a collection with ScriptEngineTests: the watchdog test below abandons a thread
// that keeps burning CPU on a 2-core box, which thins the headroom ScriptEngineTests'
// < 2s timing assertion depends on if the two ran in parallel.
[Collection("script-sandbox")]
public class ScriptHandlerTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"status":{"type":"string"},"name":{"type":"string"}}}""";

  private static async Task<(Data.Entity e, FakeNotifier notifier, ScriptHandler handler)> SetupAsync(Data.TietueDbContext db, bool enabled = true)
  {
    await new TypeRepository(db).DefineAsync("task", Schema);
    var entities = new EntityRepository(db, new SchemaValidator());
    var e = await entities.CreateAsync("task", JsonNode.Parse("""{"name":"Jari","status":"open"}"""), []);
    var notifier = new FakeNotifier();
    var applier = new ScriptEffectApplier(entities, notifier, new TriggerRepository(db, TestConfig.Default), new FakeAgentRunner(), new Lazy<HandlerRegistry>(() => new HandlerRegistry([new NotifyHandler(notifier)])), TestConfig.Default);
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

  [Fact]
  public async Task Script_exceeding_the_wall_clock_budget_is_abandoned_without_stalling_the_tick()
  {
    // Dynamically-constructed RegExp escapes Jint's RegexTimeout constraint and stalls
    // ~5s (documented in ScriptEngine). The tick holds the Postgres advisory lock while a
    // handler runs, so the budget — not the sandbox — is what bounds the damage.
    const string source = "var re = new RegExp('(a+)+$'); return { hit: re.test('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab') };";
    using var db = TestDb.New();
    var (e, notifier, _) = await SetupAsync(db);
    var entities = new EntityRepository(db, new SchemaValidator());
    var applier = new ScriptEffectApplier(entities, notifier, new TriggerRepository(db, TestConfig.Default), new FakeAgentRunner(), new Lazy<HandlerRegistry>(() => new HandlerRegistry([new NotifyHandler(notifier)])), TestConfig.Default);
    var handler = new ScriptHandler(new ScriptEngine(), applier, new ScriptOptions { Enabled = true, TimeoutSeconds = 1 });
    var config = /*lang=json,strict*/ $$"""{"source":"{{source}}","capabilities":[]}""";

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));
    sw.Stop();

    Assert.Equal("timeout", result.Status);
    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
      $"handler took {sw.Elapsed.TotalSeconds:F1}s; the 1s budget must bound the tick");
    // No effects may be applied from a script that never produced a result.
    Assert.Empty(notifier.Sent);
  }
}
