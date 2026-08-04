using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Scripts;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ScriptEffectApplierTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"status":{"type":"string"},"name":{"type":"string"},"count":{"type":"number"}}}""";

  private static async Task<(Data.Entity entity, EntityRepository entities, FakeMcpInvoker mcp, ScriptEffectApplier applier)> SetupAsync(Data.TietueDbContext db)
  {
    await new TypeRepository(db).DefineAsync("task", Schema);
    var entities = new EntityRepository(db, new SchemaValidator());
    var e = await entities.CreateAsync("task", JsonNode.Parse("""{"name":"x","status":"open"}"""), []);
    var mcp = new FakeMcpInvoker();
    return (e, entities, mcp, new ScriptEffectApplier(entities, mcp));
  }

  [Fact]
  public async Task Applies_multiple_setfields_in_one_update()
  {
    using var db = TestDb.New();
    var (e, entities, _, applier) = await SetupAsync(db);
    var effects = ScriptEffects.Parse(/*lang=json,strict*/
      """{"setField":[{"path":"status","value":"done"},{"path":"count","value":3}]}""");

    var applied = await applier.ApplyAsync(e, effects, ["setField"]);

    Assert.Contains("setField:2", applied);
    var reloaded = await entities.GetAsync(e.Id);
    Assert.Equal("done", reloaded!.Data.RootElement.GetProperty("status").GetString());
    Assert.Equal(3, reloaded.Data.RootElement.GetProperty("count").GetInt32());
  }

  [Fact]
  public async Task Denies_setfield_without_grant()
  {
    using var db = TestDb.New();
    var (e, entities, _, applier) = await SetupAsync(db);
    var effects = ScriptEffects.Parse(/*lang=json,strict*/ """{"setField":[{"path":"status","value":"done"}]}""");

    var applied = await applier.ApplyAsync(e, effects, []);

    Assert.Contains("setField:denied", applied);
    var reloaded = await entities.GetAsync(e.Id);
    Assert.Equal("open", reloaded!.Data.RootElement.GetProperty("status").GetString());
  }

  [Fact]
  public async Task Invokes_granted_mcp_call_with_args()
  {
    using var db = TestDb.New();
    var (e, _, mcp, applier) = await SetupAsync(db);
    var effects = ScriptEffects.Parse(/*lang=json,strict*/
      """{"mcpCall":[{"tool":"send_notification","args":{"message":"hi"}}]}""");

    var applied = await applier.ApplyAsync(e, effects, ["mcp:send_notification"]);

    Assert.Contains("mcpCall:send_notification:ok", applied);
    var (Tool, ArgsJson) = Assert.Single(mcp.Calls);
    Assert.Equal("send_notification", Tool);
    Assert.Contains("hi", ArgsJson);
  }

  [Fact]
  public async Task Denies_ungranted_mcp_call()
  {
    using var db = TestDb.New();
    var (e, _, mcp, applier) = await SetupAsync(db);
    var effects = ScriptEffects.Parse(/*lang=json,strict*/ """{"mcpCall":[{"tool":"display_show","args":{}}]}""");

    var applied = await applier.ApplyAsync(e, effects, ["mcp:send_notification"]);

    Assert.Contains("mcpCall:display_show:denied", applied);
    Assert.Empty(mcp.Calls);
  }

  [Fact]
  public async Task Grant_matching_is_per_tool_and_case_insensitive()
  {
    using var db = TestDb.New();
    var (e, _, mcp, applier) = await SetupAsync(db);
    var effects = ScriptEffects.Parse(/*lang=json,strict*/ """{"mcpCall":[{"tool":"display_show","args":{}}]}""");

    var applied = await applier.ApplyAsync(e, effects, ["MCP:DISPLAY_SHOW"]);

    Assert.Contains("mcpCall:display_show:ok", applied);
  }

  [Fact]
  public async Task Mcp_failure_is_recorded_and_does_not_throw()
  {
    using var db = TestDb.New();
    var (e, _, mcp, applier) = await SetupAsync(db);
    mcp.NextException = new InvalidOperationException("server unreachable");
    var effects = ScriptEffects.Parse(/*lang=json,strict*/ """{"mcpCall":[{"tool":"display_show","args":{}}]}""");

    var applied = await applier.ApplyAsync(e, effects, ["mcp:display_show"]);

    Assert.Contains(applied, a => a.StartsWith("mcpCall:display_show:error:", StringComparison.Ordinal) && a.Contains("unreachable"));
  }

  [Fact]
  public async Task Unknown_tool_is_recorded_as_error()
  {
    using var db = TestDb.New();
    var (e, _, mcp, applier) = await SetupAsync(db);
    mcp.NextResult = null;
    var effects = ScriptEffects.Parse(/*lang=json,strict*/ """{"mcpCall":[{"tool":"nope","args":{}}]}""");

    var applied = await applier.ApplyAsync(e, effects, ["mcp:nope"]);

    Assert.Contains("mcpCall:nope:error:no such tool", applied);
  }

  [Fact]
  public async Task Mcp_calls_beyond_the_cap_are_skipped()
  {
    using var db = TestDb.New();
    var (e, _, mcp, applier) = await SetupAsync(db);
    var calls = string.Join(",", Enumerable.Range(0, 12).Select(_ => /*lang=json,strict*/ """{"tool":"t","args":{}}"""));
    var effects = ScriptEffects.Parse($$"""{"mcpCall":[{{calls}}]}""");

    var applied = await applier.ApplyAsync(e, effects, ["mcp:t"]);

    Assert.Equal(ScriptEffectApplier.MaxMcpCalls, mcp.Calls.Count);
    Assert.Contains("mcpCall:skipped:limit", applied);
  }

  [Fact]
  public async Task Denied_calls_do_not_consume_the_invocation_budget()
  {
    using var db = TestDb.New();
    var (e, _, mcp, applier) = await SetupAsync(db);
    var denied = string.Join(",", Enumerable.Range(0, 5).Select(_ => /*lang=json,strict*/ """{"tool":"x","args":{}}"""));
    var granted = string.Join(",", Enumerable.Range(0, 10).Select(_ => /*lang=json,strict*/ """{"tool":"t","args":{}}"""));
    var effects = ScriptEffects.Parse($$"""{"mcpCall":[{{denied}},{{granted}}]}""");

    var applied = await applier.ApplyAsync(e, effects, ["mcp:t"]);

    Assert.Equal(ScriptEffectApplier.MaxMcpCalls, mcp.Calls.Count);
    Assert.Equal(5, applied.Count(a => a == "mcpCall:x:denied"));
    Assert.Equal(10, applied.Count(a => a == "mcpCall:t:ok"));
    Assert.DoesNotContain("mcpCall:skipped:limit", applied);
  }

  [Fact]
  public async Task Hung_mcp_call_times_out_within_the_effects_budget()
  {
    using var db = TestDb.New();
    var (e, _, mcp, applier) = await SetupAsync(db);
    mcp.Hang = true;
    var effects = ScriptEffects.Parse(/*lang=json,strict*/ """{"mcpCall":[{"tool":"t","args":{}},{"tool":"t","args":{}}]}""");

    var applied = await applier.ApplyAsync(e, effects, ["mcp:t"], effectsBudget: TimeSpan.FromMilliseconds(100));

    Assert.Contains("mcpCall:t:error:timeout", applied);
    Assert.Contains("mcpCall:skipped:timeout", applied);
    Assert.Single(mcp.Calls);
  }

  [Fact]
  public async Task Genuine_cancellation_propagates_out_of_apply()
  {
    using var db = TestDb.New();
    var (e, _, mcp, applier) = await SetupAsync(db);
    mcp.Hang = true;
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var effects = ScriptEffects.Parse(/*lang=json,strict*/ """{"mcpCall":[{"tool":"t","args":{}}]}""");

    await Assert.ThrowsAnyAsync<OperationCanceledException>(
      () => applier.ApplyAsync(e, effects, ["mcp:t"], ct: cts.Token));
  }

  [Fact]
  public async Task Setfield_failure_is_isolated_and_mcp_calls_still_run()
  {
    using var db = TestDb.New();
    var (e, _, mcp, applier) = await SetupAsync(db);
    var effects = ScriptEffects.Parse(/*lang=json,strict*/
      """{"setField":[{"path":"count","value":"not-a-number"}],"mcpCall":[{"tool":"t","args":{}}]}""");

    var applied = await applier.ApplyAsync(e, effects, ["setField", "mcp:t"]);

    Assert.Contains(applied, a => a.StartsWith("setField:error:", StringComparison.Ordinal));
    Assert.Contains("mcpCall:t:ok", applied);
    Assert.Single(mcp.Calls);
  }

  [Fact]
  public async Task Reserved_paths_are_denied_case_insensitively_and_rest_applied()
  {
    using var db = TestDb.New();
    var (e, entities, _, applier) = await SetupAsync(db);
    var effects = ScriptEffects.Parse(/*lang=json,strict*/
      """{"setField":[{"path":"Code","value":"evil"},{"path":"status","value":"done"}]}""");

    var applied = await applier.ApplyAsync(e, effects, ["setField"], reservedPaths: ["code", "grants", "allowedHosts", "enabled"]);

    Assert.Contains("setField:denied:reserved:Code", applied);
    Assert.Contains("setField:1", applied);
    var reloaded = await entities.GetAsync(e.Id);
    Assert.Equal("done", reloaded!.Data.RootElement.GetProperty("status").GetString());
    Assert.False(reloaded.Data.RootElement.TryGetProperty("Code", out _));
  }
}
