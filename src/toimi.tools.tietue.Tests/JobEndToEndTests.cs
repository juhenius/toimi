using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Provisioning;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Scripts;
using toimi.tools.tietue.Seed;
using toimi.tools.tietue.Tools;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

/// <summary>
/// End-to-end over the seeded job type: TypeSeeder → EntityRepository create with
/// TriggerProvisioner copy-down → RunTriggerTool firing the real ScriptHandler.
/// Covers the wiring the per-component tests take for granted.
/// </summary>
public class JobEndToEndTests
{
  private const string JobJson = /*lang=json,strict*/ """
  {"name":"weather","code":"export default () => ({})",
   "allowedHosts":["api.open-meteo.com"],"grants":["setField","mcp:display_show"],
   "startAt":"2030-01-01T06:00:00Z","rrule":"FREQ=DAILY"}
  """;

  [Fact]
  public async Task Seeded_job_provisions_script_trigger_and_run_trigger_executes_it()
  {
    using var db = TestDb.New();
    await new TypeSeeder(new TypeRepository(db)).SeedAsync();
    var triggers = new TriggerRepository(db, TestConfig.Default);
    var entities = new EntityRepository(db, new SchemaValidator(), provisioner: new TriggerProvisioner(triggers));

    var e = await entities.CreateAsync(ScriptHandler.JobTypeName, JsonNode.Parse(JobJson), []);

    // Copy-down: the seeded type's DefaultTriggers became a concrete script trigger.
    var trigger = Assert.Single(await triggers.ListByEntityAsync(e.Id));
    Assert.Equal("script", trigger.HandlerKind);
    Assert.Contains("\"fromEntity\":true", trigger.HandlerConfig);
    Assert.Contains("2030-01-01T06:00:00Z", trigger.Schedule);
    Assert.Contains("FREQ=DAILY", trigger.Schedule);
    Assert.Equal(new DateTimeOffset(2030, 1, 1, 6, 0, 0, TimeSpan.Zero), trigger.NextFireAt);

    var suoritin = new FakeSuoritinClient();
    var handler = new ScriptHandler(
      suoritin, new ScriptEffectApplier(entities, new FakeMcpInvoker()), new RunTokenStore(),
      new ScriptOptions(), new SuoritinOptions());
    var tool = new RunTriggerTool(db, new OccurrenceRunner(db, new HandlerRegistry([handler]), new EntityEventStore(db)));

    var result = await tool.RunTrigger(trigger.Id.ToString());

    // The request carried the entity's own code, hosts, and grants (fromEntity mode).
    var request = Assert.Single(suoritin.Requests);
    Assert.Equal("export default () => ({})", request.Code);
    Assert.Equal(["api.open-meteo.com"], request.AllowedHosts);
    Assert.Equal(["setField", "mcp:display_show"], request.Grants);
    Assert.Contains("\"status\":\"ran\"", result);

    var ev = Assert.Single(db.EntityEvents.Where(x => x.EntityId == e.Id));
    Assert.Equal("script", ev.Kind);
    Assert.Equal("ran", ev.Status);
  }
}
