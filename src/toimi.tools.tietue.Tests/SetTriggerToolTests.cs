using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Tools;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class SetTriggerToolTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"name":{"type":"string"}}}""";

  private static HandlerRegistry Handlers()
  {
    return new HandlerRegistry([new NotifyHandler(new FakeNotifier())]);
  }

  private static async Task<Data.Entity> SeedEntityAsync(Data.TietueDbContext db)
  {
    await new TypeRepository(db).DefineAsync("task", Schema);
    var entities = new EntityRepository(db, new SchemaValidator());
    return await entities.CreateAsync("task", JsonNode.Parse("""{"name":"x"}"""), []);
  }

  [Fact]
  public async Task Sets_trigger_on_existing_entity()
  {
    using var db = TestDb.New();
    var e = await SeedEntityAsync(db);
    var tool = new SetTriggerTool(new TriggerRepository(db, TestConfig.Default), db, Handlers(), TestConfig.Default);

    var result = await tool.SetTrigger(e.Id.ToString(), /*lang=json,strict*/ """{"at":"2026-06-20T09:00:00Z"}""", "notify", /*lang=json,strict*/ """{"titleTemplate":"hi"}""");

    using var doc = JsonDocument.Parse(result);
    Assert.True(doc.RootElement.TryGetProperty("id", out _));
    Assert.Single(await new TriggerRepository(db, TestConfig.Default).ListByEntityAsync(e.Id));
  }

  [Fact]
  public async Task Rejects_non_guid_entity_id()
  {
    using var db = TestDb.New();
    var tool = new SetTriggerTool(new TriggerRepository(db, TestConfig.Default), db, Handlers(), TestConfig.Default);

    var result = await tool.SetTrigger("not-a-guid", /*lang=json,strict*/ """{"at":"2026-06-20T09:00:00Z"}""", "notify");

    Assert.Contains("Invalid entityId", result);
  }

  [Fact]
  public async Task Rejects_unknown_entity()
  {
    using var db = TestDb.New();
    var tool = new SetTriggerTool(new TriggerRepository(db, TestConfig.Default), db, Handlers(), TestConfig.Default);

    var result = await tool.SetTrigger(Guid.NewGuid().ToString(), /*lang=json,strict*/ """{"at":"2026-06-20T09:00:00Z"}""", "notify");

    Assert.Contains("No entity found", result);
    Assert.Empty(await db.Triggers.ToListAsync());
  }

  [Fact]
  public async Task Rejects_unknown_handler_kind()
  {
    using var db = TestDb.New();
    var e = await SeedEntityAsync(db);
    var tool = new SetTriggerTool(new TriggerRepository(db, TestConfig.Default), db, Handlers(), TestConfig.Default);

    var result = await tool.SetTrigger(e.Id.ToString(), /*lang=json,strict*/ """{"at":"2026-06-20T09:00:00Z"}""", "messsage");

    Assert.Contains("Unknown handlerKind", result);
    Assert.Contains("notify", result);
    Assert.Empty(await db.Triggers.ToListAsync());
  }

  [Fact]
  public async Task Rejects_schedule_that_never_fires()
  {
    using var db = TestDb.New();
    var e = await SeedEntityAsync(db);
    var tool = new SetTriggerTool(new TriggerRepository(db, TestConfig.Default), db, Handlers(), TestConfig.Default);

    var result = await tool.SetTrigger(e.Id.ToString(), /*lang=json,strict*/ """{"start":"2020-01-01T00:00:00Z","rrule":"FREQ=YEARLY;COUNT=1"}""", "notify");

    Assert.Contains("does not resolve to a future fire time", result);
    Assert.Empty(await db.Triggers.ToListAsync());
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"start":"2026-01-01T00:00:00Z","rrule":"FREQ=MINUTELY;INTERVAL=30;BYHOUR=9","tz":"Europe/Helsinki"}""")]
  // No tz: WithDefaultTimeZone stamps the user's DST zone, so the pre-check must run on the stamped schedule.
  [InlineData(/*lang=json,strict*/ """{"start":"2026-01-01T00:00:00Z","rrule":"FREQ=MINUTELY;INTERVAL=30;BYHOUR=9"}""")]
  public async Task Rejects_subdaily_by_part_rule_in_dst_timezone_with_specific_message(string schedule)
  {
    using var db = TestDb.New();
    var e = await SeedEntityAsync(db);
    var tool = new SetTriggerTool(new TriggerRepository(db, TestConfig.Default), db, Handlers(), TestConfig.Default);

    var result = await tool.SetTrigger(e.Id.ToString(), schedule, "notify");

    Assert.Contains("not supported in DST timezones", result);
    Assert.Contains("tz:\"UTC\"", result);
    Assert.Empty(await db.Triggers.ToListAsync());
  }

  [Fact]
  public async Task Accepts_subdaily_by_part_rule_with_utc_tz()
  {
    // The documented escape hatch: tz "UTC" has no DST transitions, so the rule is safe.
    using var db = TestDb.New();
    var e = await SeedEntityAsync(db);
    var tool = new SetTriggerTool(new TriggerRepository(db, TestConfig.Default), db, Handlers(), TestConfig.Default);

    var result = await tool.SetTrigger(
      e.Id.ToString(),
      /*lang=json,strict*/ """{"start":"2026-01-01T00:00:00Z","rrule":"FREQ=MINUTELY;INTERVAL=30;BYHOUR=9","tz":"UTC"}""",
      "notify");

    using var doc = JsonDocument.Parse(result);
    Assert.True(doc.RootElement.TryGetProperty("id", out _));
    Assert.Single(await db.Triggers.ToListAsync());
  }

  [Fact]
  public async Task Rejects_malformed_schedule()
  {
    using var db = TestDb.New();
    var e = await SeedEntityAsync(db);
    var tool = new SetTriggerTool(new TriggerRepository(db, TestConfig.Default), db, Handlers(), TestConfig.Default);

    var result = await tool.SetTrigger(e.Id.ToString(), "not json", "notify");

    Assert.Contains("does not resolve to a future fire time", result);
    Assert.Empty(await db.Triggers.ToListAsync());
  }
}
