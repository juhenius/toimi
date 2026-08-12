using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Provisioning;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ExpiryReconcilerTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"name":{"type":"string"},"expiresAt":{"type":"string"}}}""";

  private static async Task<EntityRepository> SetupAsync(Data.TietueDbContext db, string? behaviors)
  {
    await new TypeRepository(db).DefineAsync("temp", Schema, behaviors);
    var triggers = new TriggerRepository(db, TestConfig.Default);
    var reconciler = new ExpiryReconciler(db, triggers);
    return new EntityRepository(db, new SchemaValidator(), [new ExpiryBehavior(reconciler)]);
  }

  private const string DeleteExpiry = /*lang=json,strict*/ """[{"behavior":"Expiry","config":{"field":"expiresAt"}}]""";
  private const string AgentExpiry = /*lang=json,strict*/ """[{"behavior":"Expiry","config":{"field":"expiresAt","prompt":"check if still needed"}}]""";

  [Fact]
  public async Task Provisions_delete_trigger_on_create()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, DeleteExpiry);
    var e = await repo.CreateAsync("temp", JsonNode.Parse("""{"name":"x","expiresAt":"2026-09-01T00:00:00Z"}"""), []);

    var t = await db.Triggers.SingleAsync(x => x.EntityId == e.Id && x.Source == "expiry");
    Assert.Equal("delete", t.HandlerKind);
    Assert.NotNull(t.NextFireAt);
  }

  [Fact]
  public async Task Uses_message_handler_when_prompt_present()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, AgentExpiry);
    var e = await repo.CreateAsync("temp", JsonNode.Parse("""{"name":"x","expiresAt":"2026-09-01T00:00:00Z"}"""), []);

    var t = await db.Triggers.SingleAsync(x => x.EntityId == e.Id && x.Source == "expiry");
    Assert.Equal("message", t.HandlerKind);
    Assert.Contains("promptTemplate", t.HandlerConfig);
    Assert.Contains("check if still needed", t.HandlerConfig);
  }

  [Fact]
  public async Task No_trigger_when_field_absent()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, DeleteExpiry);
    var e = await repo.CreateAsync("temp", JsonNode.Parse("""{"name":"x"}"""), []);

    Assert.False(await db.Triggers.AnyAsync(x => x.EntityId == e.Id && x.Source == "expiry"));
  }

  [Fact]
  public async Task Update_moves_the_trigger()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, DeleteExpiry);
    var e = await repo.CreateAsync("temp", JsonNode.Parse("""{"name":"x","expiresAt":"2026-09-01T00:00:00Z"}"""), []);

    await repo.UpdateAsync(e.Id, JsonNode.Parse("""{"name":"x","expiresAt":"2027-01-01T00:00:00Z"}"""), null);

    var t = await db.Triggers.SingleAsync(x => x.EntityId == e.Id && x.Source == "expiry");
    Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), t.NextFireAt);
  }

  [Fact]
  public async Task Update_removing_field_drops_the_trigger()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, DeleteExpiry);
    var e = await repo.CreateAsync("temp", JsonNode.Parse("""{"name":"x","expiresAt":"2026-09-01T00:00:00Z"}"""), []);

    await repo.UpdateAsync(e.Id, JsonNode.Parse("""{"name":"x"}"""), null);

    Assert.False(await db.Triggers.AnyAsync(x => x.EntityId == e.Id && x.Source == "expiry"));
  }

  [Fact]
  public async Task Reconcile_does_not_duplicate_triggers()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, DeleteExpiry);
    var e = await repo.CreateAsync("temp", JsonNode.Parse("""{"name":"x","expiresAt":"2026-09-01T00:00:00Z"}"""), []);
    await repo.UpdateAsync(e.Id, JsonNode.Parse("""{"name":"y","expiresAt":"2026-09-01T00:00:00Z"}"""), null);

    Assert.Equal(1, await db.Triggers.CountAsync(x => x.EntityId == e.Id && x.Source == "expiry"));
  }

  [Fact]
  public async Task Garbage_expiry_date_does_not_arm_a_zombie_trigger()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, DeleteExpiry);
    var e = await repo.CreateAsync("temp", JsonNode.Parse("""{"name":"x","expiresAt":"soon"}"""), []);

    var t = await db.Triggers.SingleOrDefaultAsync(x => x.EntityId == e.Id && x.Source == "expiry");
    Assert.Null(t);
  }

  [Fact]
  public async Task Past_expiry_date_arms_an_immediately_due_trigger()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, DeleteExpiry);
    var e = await repo.CreateAsync("temp", JsonNode.Parse("""{"name":"x","expiresAt":"2020-01-01T00:00:00Z"}"""), []);

    var t = await db.Triggers.SingleAsync(x => x.EntityId == e.Id && x.Source == "expiry");
    Assert.True(t.Enabled);
    Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), t.NextFireAt);
  }

  private sealed class CapturingLogger : ILogger<ExpiryReconciler>
  {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
      return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
      return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
      Entries.Add((logLevel, formatter(state, exception)));
    }
  }

  private static async Task<(EntityRepository repo, CapturingLogger log)> LoggedSetupAsync(Data.TietueDbContext db)
  {
    await new TypeRepository(db).DefineAsync("temp", Schema, DeleteExpiry);
    var log = new CapturingLogger();
    var reconciler = new ExpiryReconciler(db, new TriggerRepository(db, TestConfig.Default), log);
    return (new EntityRepository(db, new SchemaValidator(), [new ExpiryBehavior(reconciler)]), log);
  }

  [Fact]
  public async Task Garbage_expiry_date_logs_a_warning()
  {
    using var db = TestDb.New();
    var (repo, log) = await LoggedSetupAsync(db);

    await repo.CreateAsync("temp", JsonNode.Parse("""{"name":"x","expiresAt":"soon"}"""), []);

    var (Level, Message) = Assert.Single(log.Entries);
    Assert.Equal(LogLevel.Warning, Level);
    Assert.Contains("expiresAt", Message);
  }

  [Fact]
  public async Task Absent_expiry_field_stays_silent()
  {
    using var db = TestDb.New();
    var (repo, log) = await LoggedSetupAsync(db);

    await repo.CreateAsync("temp", JsonNode.Parse("""{"name":"x"}"""), []);

    Assert.Empty(log.Entries);
  }
}
