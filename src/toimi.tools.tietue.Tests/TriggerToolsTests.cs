using System.Text.Json;
using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Tools;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using toimi.tools.tietue.Webhooks;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TriggerToolsTests
{
  [Fact]
  public async Task SetTrigger_then_ListTriggers_includes_it()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);
    await new TypeRepository(db).DefineAsync("task", /*lang=json,strict*/ """{"type":"object","properties":{"name":{"type":"string"}}}""");
    var entity = await new EntityRepository(db, new SchemaValidator()).CreateAsync("task", JsonNode.Parse("""{"name":"x"}"""), []);
    var entityId = entity.Id;

    var set = await new SetTriggerTool(repo, db, new HandlerRegistry([new NotifyHandler(new FakeNotifier())]), new WebhookOptions()).SetTrigger(entityId.ToString(), /*lang=json,strict*/ """{"at":"2026-06-20T09:00:00Z"}""", "notify", /*lang=json,strict*/ """{"titleTemplate":"hi"}""");
    Assert.Contains("\"id\"", set);

    var list = await new ListTriggersTool(repo, new WebhookOptions()).ListTriggers(entityId.ToString());
    using var doc = JsonDocument.Parse(list);
    Assert.Equal(1, doc.RootElement.GetArrayLength());
  }

  [Fact]
  public async Task ListTriggers_includes_url_for_webhook_rows_only()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);
    var entityId = Guid.NewGuid();
    await repo.CreateAsync(entityId, /*lang=json,strict*/ """{"at":"2026-06-20T09:00:00Z"}""", "notify", /*lang=json,strict*/ """{"titleTemplate":"hi"}""", DateTimeOffset.UtcNow);
    var hook = await repo.CreateAsync(entityId, /*lang=json,strict*/ """{"webhook":{}}""", "notify", /*lang=json,strict*/ """{"titleTemplate":"hi"}""", DateTimeOffset.UtcNow);

    var list = await new ListTriggersTool(repo, new WebhookOptions { PublicBaseUrl = "https://toimi.example" }).ListTriggers(entityId.ToString());

    using var doc = JsonDocument.Parse(list);
    var rows = doc.RootElement.EnumerateArray().ToArray();
    var timeRow = rows.Single(r => r.GetProperty("nextFireAt").ValueKind == JsonValueKind.String);
    var hookRow = rows.Single(r => r.GetProperty("nextFireAt").ValueKind == JsonValueKind.Null);
    Assert.False(timeRow.TryGetProperty("url", out _));
    Assert.Equal($"https://toimi.example/hooks/{hook.Id}/{hook.Secret}", hookRow.GetProperty("url").GetString());
    Assert.Equal(hook.Secret, hookRow.GetProperty("secret").GetString());
  }

  [Fact]
  public async Task ListTriggers_returns_the_secret_even_without_a_public_base_url()
  {
    // ADR 0001: the secret is retrievable through the trigger tools — otherwise a lost
    // creation response makes the capability URL unrecoverable.
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);
    var entityId = Guid.NewGuid();
    var hook = await repo.CreateAsync(entityId, /*lang=json,strict*/ """{"webhook":{}}""", "notify", /*lang=json,strict*/ """{"titleTemplate":"hi"}""", DateTimeOffset.UtcNow);

    var list = await new ListTriggersTool(repo, new WebhookOptions()).ListTriggers(entityId.ToString());

    using var doc = JsonDocument.Parse(list);
    var row = doc.RootElement.EnumerateArray().Single();
    Assert.Equal(JsonValueKind.Null, row.GetProperty("url").ValueKind);
    Assert.Equal(hook.Secret, row.GetProperty("secret").GetString());
  }

  [Fact]
  public async Task DeleteTrigger_removes_it()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);
    var t = await repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"at":"2026-06-20T09:00:00Z"}""", "notify", null, DateTimeOffset.UtcNow);

    Assert.Contains("deleted", await new DeleteTriggerTool(repo).DeleteTrigger(t.Id.ToString()));
  }

  [Fact]
  public async Task CompleteOccurrence_records_completion()
  {
    using var db = TestDb.New();
    var store = new EntityEventStore(db);
    var entityId = Guid.NewGuid();

    var result = await new CompleteOccurrenceTool(store).CompleteOccurrence(entityId.ToString(), "2026-06-20T09:00:00Z");

    var occ = new DateTimeOffset(2026, 6, 20, 9, 0, 0, TimeSpan.Zero);
    Assert.Contains("completed", result);
    Assert.Equal(ClaimResult.AlreadyHandled, await store.TryClaimAsync(entityId, occ, "notify", occ));
  }
}
