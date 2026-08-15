using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Tools;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ActivateToolTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"name":{"type":"string"}}}""";

  private static async Task<(EntityRepository entities, FakeAgentRunner runner, EntityEventStore events, TriggerRepository triggers, Guid entityId)> SetupAsync(Data.TietueDbContext db)
  {
    await new TypeRepository(db).DefineAsync("task", Schema);
    var entities = new EntityRepository(db, new SchemaValidator());
    var e = await entities.CreateAsync("task", JsonNode.Parse("""{"name":"x"}"""), []);
    return (entities, new FakeAgentRunner(), new EntityEventStore(db), new TriggerRepository(db, TestConfig.Default), e.Id);
  }

  [Fact]
  public async Task Activate_now_runs_agent_and_records_event()
  {
    using var db = TestDb.New();
    var (entities, runner, events, triggers, id) = await SetupAsync(db);
    var tool = new ActivateTool(entities, runner, events, triggers);

    var result = await tool.Activate(id.ToString(), "do the thing", null);

    var (Entity, Prompt, _) = Assert.Single(runner.Runs);
    Assert.Equal("do the thing", Prompt);
    Assert.Contains("ok", result);
  }

  [Fact]
  public async Task Activate_with_when_schedules_a_message_trigger()
  {
    using var db = TestDb.New();
    var (entities, runner, events, triggers, id) = await SetupAsync(db);
    var tool = new ActivateTool(entities, runner, events, triggers);

    var result = await tool.Activate(id.ToString(), "later thing", "2026-07-01T09:00:00Z");

    Assert.Empty(runner.Runs);
    var t = Assert.Single(await triggers.ListByEntityAsync(id));
    Assert.Equal("message", t.HandlerKind);
    Assert.Contains("later thing", t.HandlerConfig);
  }

  [Fact]
  public async Task Activate_now_with_smart_model_pins_the_run()
  {
    using var db = TestDb.New();
    var (entities, runner, events, triggers, id) = await SetupAsync(db);
    var tool = new ActivateTool(entities, runner, events, triggers);

    await tool.Activate(id.ToString(), "hard task", null, "smart");

    Assert.Equal(Toimi.Core.Llm.ModelTier.Smart, Assert.Single(runner.Runs).Tier);
  }

  [Fact]
  public async Task Activate_rejects_an_unknown_model()
  {
    using var db = TestDb.New();
    var (entities, runner, events, triggers, id) = await SetupAsync(db);
    var tool = new ActivateTool(entities, runner, events, triggers);

    var result = await tool.Activate(id.ToString(), "task", null, "cheap");

    Assert.Contains("Invalid 'model'", result);
    Assert.Empty(runner.Runs);
  }

  [Fact]
  public async Task Activate_scheduled_with_model_writes_the_pin_into_the_trigger_config()
  {
    using var db = TestDb.New();
    var (entities, runner, events, triggers, id) = await SetupAsync(db);
    var tool = new ActivateTool(entities, runner, events, triggers);

    await tool.Activate(id.ToString(), "nightly analysis", "2026-07-01T09:00:00Z", "SMART");

    var t = Assert.Single(await triggers.ListByEntityAsync(id));
    Assert.Contains("\"model\":\"smart\"", t.HandlerConfig);
  }

  [Fact]
  public async Task Activate_now_records_token_usage_in_event()
  {
    using var db = TestDb.New();
    var (entities, runner, events, triggers, id) = await SetupAsync(db);
    runner.Result = new Agents.AgentRunResult(true, "ok", null, null, PromptTokens: 1200, CompletionTokens: 340);
    var tool = new ActivateTool(entities, runner, events, triggers);

    await tool.Activate(id.ToString(), "do the thing", null);

    var evt = Assert.Single(db.EntityEvents.Where(e => e.EntityId == id && e.Kind == "message"));
    Assert.Contains("\"promptTokens\":1200", evt.Result);
    Assert.Contains("\"completionTokens\":340", evt.Result);
  }

  [Fact]
  public async Task Activate_unknown_entity_returns_message()
  {
    using var db = TestDb.New();
    var (entities, runner, events, triggers, _) = await SetupAsync(db);
    var tool = new ActivateTool(entities, runner, events, triggers);

    var result = await tool.Activate(Guid.NewGuid().ToString(), "x", null);

    Assert.Contains("not found", result);
  }
}
