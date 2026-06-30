using System.Text.Json;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Tools;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TriggerToolsTests
{
  [Fact]
  public async Task SetTrigger_then_ListTriggers_includes_it()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db);
    var entityId = Guid.NewGuid();

    var set = await new SetTriggerTool(repo).SetTrigger(entityId.ToString(), /*lang=json,strict*/ """{"at":"2026-06-20T09:00:00Z"}""", "notify", /*lang=json,strict*/ """{"titleTemplate":"hi"}""");
    Assert.Contains("\"id\"", set);

    var list = await new ListTriggersTool(repo).ListTriggers(entityId.ToString());
    using var doc = JsonDocument.Parse(list);
    Assert.Equal(1, doc.RootElement.GetArrayLength());
  }

  [Fact]
  public async Task DeleteTrigger_removes_it()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db);
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

    Assert.Contains("completed", result);
    Assert.True(await store.OccurrenceHandledAsync(entityId, new DateTimeOffset(2026, 6, 20, 9, 0, 0, TimeSpan.Zero), "notify"));
  }
}
