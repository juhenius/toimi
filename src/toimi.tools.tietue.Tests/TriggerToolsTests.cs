using System.Text.Json;
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

    var set = await new SetTriggerTool(repo, db, new HandlerRegistry([new NotifyHandler(new FakeNotifier())]), TestConfig.Default).SetTrigger(entityId.ToString(), /*lang=json,strict*/ """{"at":"2026-06-20T09:00:00Z"}""", "notify", /*lang=json,strict*/ """{"titleTemplate":"hi"}""");
    Assert.Contains("\"id\"", set);

    var list = await new ListTriggersTool(repo).ListTriggers(entityId.ToString());
    using var doc = JsonDocument.Parse(list);
    Assert.Equal(1, doc.RootElement.GetArrayLength());
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
