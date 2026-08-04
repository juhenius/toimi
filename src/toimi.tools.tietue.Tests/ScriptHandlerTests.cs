using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Scripts;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ScriptHandlerTests
{
  private const string Schema = /*lang=json,strict*/
    """{"type":"object","properties":{"status":{"type":"string"},"name":{"type":"string"},"code":{"type":"string"},"allowedHosts":{"type":"array"},"grants":{"type":"array"},"enabled":{"type":"boolean"}}}""";

  private static async Task<(Data.Entity e, FakeSuoritinClient suoritin, FakeMcpInvoker mcp, RunTokenStore tokens, ScriptHandler handler)> SetupAsync(
    Data.TietueDbContext db, string entityJson = /*lang=json,strict*/ """{"name":"Jari","status":"open"}""", bool enabled = true, int timeoutSeconds = 20)
  {
    await new TypeRepository(db).DefineAsync("task", Schema);
    var entities = new EntityRepository(db, new SchemaValidator());
    var e = await entities.CreateAsync("task", JsonNode.Parse(entityJson), []);
    var suoritin = new FakeSuoritinClient();
    var mcp = new FakeMcpInvoker();
    var tokens = new RunTokenStore();
    var handler = new ScriptHandler(
      suoritin, new ScriptEffectApplier(entities, mcp), tokens,
      new ScriptOptions { Enabled = enabled, TimeoutSeconds = timeoutSeconds }, new SuoritinOptions());
    return (e, suoritin, mcp, tokens, handler);
  }

  [Fact]
  public async Task Sends_inline_config_script_to_suoritin_and_applies_effects()
  {
    using var db = TestDb.New();
    var (e, suoritin, mcp, _, handler) = await SetupAsync(db);
    suoritin.NextResult = new(true, /*lang=json,strict*/ """{"mcpCall":[{"tool":"send_notification","args":{"message":"hi"}}]}""", ["[log] ran"], null, 42);
    var config = /*lang=json,strict*/
      """{"source":"export default () => ({})","capabilities":["mcp:send_notification"],"allowedHosts":["api.example.com"]}""";

    var result = await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));

    Assert.Equal("ran", result.Status);
    var request = Assert.Single(suoritin.Requests);
    Assert.Equal("export default () => ({})", request.Code);
    Assert.Equal(["api.example.com"], request.AllowedHosts);
    Assert.Equal(["mcp:send_notification"], request.Grants);
    Assert.Equal("send_notification", Assert.Single(mcp.Calls).Tool);
    Assert.Contains("[log] ran", result.Result);
  }

  [Fact]
  public async Task From_entity_mode_reads_code_hosts_grants_from_entity_data()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, _, handler) = await SetupAsync(
      db, /*lang=json,strict*/ """{"name":"job1","code":"export default () => ({})","allowedHosts":["a.example"],"grants":["setField"]}""");

    var result = await handler.HandleAsync(new HandlerContext(e, /*lang=json,strict*/ """{"fromEntity":true}""", DateTimeOffset.UtcNow));

    Assert.Equal("ran", result.Status);
    var request = Assert.Single(suoritin.Requests);
    Assert.Equal("export default () => ({})", request.Code);
    Assert.Equal(["a.example"], request.AllowedHosts);
    Assert.Equal(["setField"], request.Grants);
  }

  [Fact]
  public async Task From_entity_mode_respects_enabled_false()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, _, handler) = await SetupAsync(
      db, /*lang=json,strict*/ """{"name":"job1","code":"export default () => ({})","enabled":false}""");

    var result = await handler.HandleAsync(new HandlerContext(e, /*lang=json,strict*/ """{"fromEntity":true}""", DateTimeOffset.UtcNow));

    Assert.Equal("disabled", result.Status);
    Assert.Empty(suoritin.Requests);
  }

  [Fact]
  public async Task Input_carries_data_and_context()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, _, handler) = await SetupAsync(db);
    var occurrence = DateTimeOffset.Parse("2026-07-31T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    var config = /*lang=json,strict*/ """{"source":"export default () => ({})","capabilities":[]}""";

    await handler.HandleAsync(new HandlerContext(e, config, occurrence));

    var input = Assert.Single(suoritin.Requests).Input;
    Assert.Equal("Jari", input.GetProperty("data").GetProperty("name").GetString());
    Assert.Equal(e.Id.ToString(), input.GetProperty("entityId").GetString());
    Assert.Equal("task", input.GetProperty("entityType").GetString());
    Assert.Equal(occurrence, DateTimeOffset.Parse(input.GetProperty("occurrence").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
  }

  [Fact]
  public async Task Llm_grant_issues_token_and_callback_url()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, tokens, handler) = await SetupAsync(db);
    var config = /*lang=json,strict*/ """{"source":"export default () => ({})","capabilities":["llm"]}""";

    await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));

    var request = Assert.Single(suoritin.Requests);
    Assert.NotNull(request.RunToken);
    Assert.Equal(new SuoritinOptions().CallbackBaseUrl, request.CallbackUrl);
    Assert.False(tokens.TryUseExtract(request.RunToken)); // revoked after the run
  }

  [Fact]
  public async Task No_llm_grant_means_no_token()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, _, handler) = await SetupAsync(db);
    var config = /*lang=json,strict*/ """{"source":"export default () => ({})","capabilities":["setField"]}""";

    await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));

    var request = Assert.Single(suoritin.Requests);
    Assert.Null(request.RunToken);
    Assert.Null(request.CallbackUrl);
  }

  [Fact]
  public async Task Script_failure_result_includes_error_and_logs()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, _, handler) = await SetupAsync(db);
    suoritin.NextResult = new(false, null, ["[log] before crash"], "boom", 10);
    var config = /*lang=json,strict*/ """{"source":"export default () => ({})","capabilities":[]}""";

    var result = await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));

    Assert.Equal("error", result.Status);
    Assert.Contains("boom", result.Result);
    Assert.Contains("before crash", result.Result);
  }

  [Fact]
  public async Task Oversized_script_error_is_truncated_in_result()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, _, handler) = await SetupAsync(db);
    suoritin.NextResult = new(false, null, [], new string('e', 3000), 10);
    var config = /*lang=json,strict*/ """{"source":"export default () => ({})","capabilities":[]}""";

    var result = await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));

    Assert.Equal("error", result.Status);
    Assert.Contains(new string('e', SuoritinClient.MaxLogChars), result.Result);
    Assert.DoesNotContain(new string('e', SuoritinClient.MaxLogChars + 1), result.Result);
  }

  [Fact]
  public async Task Suoritin_unreachable_is_an_error_not_an_exception()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, _, handler) = await SetupAsync(db);
    suoritin.NextException = new HttpRequestException("connection refused");
    var config = /*lang=json,strict*/ """{"source":"export default () => ({})","capabilities":[]}""";

    var result = await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));

    Assert.Equal("error", result.Status);
    Assert.Contains("suoritin", result.Result);
  }

  [Fact]
  public async Task Disabled_kill_switch_short_circuits()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, _, handler) = await SetupAsync(db, enabled: false);

    var result = await handler.HandleAsync(new HandlerContext(e, /*lang=json,strict*/ """{"source":"x"}""", DateTimeOffset.UtcNow));

    Assert.Equal("disabled", result.Status);
    Assert.Empty(suoritin.Requests);
  }

  [Fact]
  public async Task Missing_source_is_an_error()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, _, handler) = await SetupAsync(db);

    var result = await handler.HandleAsync(new HandlerContext(e, /*lang=json,strict*/ """{"fromEntity":true}""", DateTimeOffset.UtcNow));

    Assert.Equal("error", result.Status);
    Assert.Empty(suoritin.Requests);
  }

  [Fact]
  public async Task Reserved_job_fields_are_denied_in_from_entity_mode()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, _, handler) = await SetupAsync(
      db, /*lang=json,strict*/ """{"name":"job1","code":"export default () => ({})","grants":["setField"],"status":"open"}""");
    suoritin.NextResult = new(true, /*lang=json,strict*/
      """{"setField":[{"path":"code","value":"evil"},{"path":"status","value":"done"}]}""", [], null, 1);

    var result = await handler.HandleAsync(new HandlerContext(e, /*lang=json,strict*/ """{"fromEntity":true}""", DateTimeOffset.UtcNow));

    Assert.Equal("ran", result.Status);
    Assert.Contains("setField:denied:reserved:code", result.Result);
    Assert.Contains("setField:1", result.Result);
  }

  [Fact]
  public async Task Inline_scripts_on_job_entities_still_reserve_control_fields()
  {
    using var db = TestDb.New();
    var (_, suoritin, _, _, handler) = await SetupAsync(db);
    await new TypeRepository(db).DefineAsync("job", Schema);
    var entities = new EntityRepository(db, new SchemaValidator());
    var job = await entities.CreateAsync("job", JsonNode.Parse("""{"name":"j1","status":"open"}"""), []);
    suoritin.NextResult = new(true, /*lang=json,strict*/
      """{"setField":[{"path":"code","value":"evil"},{"path":"status","value":"done"}]}""", [], null, 1);
    var config = /*lang=json,strict*/ """{"source":"export default () => ({})","capabilities":["setField"]}""";

    var result = await handler.HandleAsync(new HandlerContext(job, config, DateTimeOffset.UtcNow));

    Assert.Equal("ran", result.Status);
    Assert.Contains("setField:denied:reserved:code", result.Result);
    Assert.Contains("setField:1", result.Result);
  }

  [Fact]
  public async Task Inline_mode_scripts_may_set_job_control_fields()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, _, handler) = await SetupAsync(db);
    suoritin.NextResult = new(true, /*lang=json,strict*/ """{"setField":[{"path":"code","value":"x"}]}""", [], null, 1);
    var config = /*lang=json,strict*/ """{"source":"export default () => ({})","capabilities":["setField"]}""";

    var result = await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));

    Assert.Equal("ran", result.Status);
    Assert.Contains("setField:1", result.Result);
    Assert.DoesNotContain("denied", result.Result);
  }

  [Fact]
  public async Task Http_client_timeout_is_classified_as_timeout()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, _, handler) = await SetupAsync(db);
    suoritin.NextException = new TaskCanceledException("HttpClient.Timeout of 25 seconds elapsed");
    var config = /*lang=json,strict*/ """{"source":"export default () => ({})","capabilities":[]}""";

    var result = await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));

    Assert.Equal("timeout", result.Status);
  }

  [Fact]
  public async Task Watchdog_bounds_a_hung_suoritin_connection()
  {
    using var db = TestDb.New();
    // timeoutSeconds -10 makes the watchdog budget (TimeoutSeconds + 10) zero, so the
    // WaitAsync path fires immediately instead of stalling the test for 10+ seconds.
    var (e, suoritin, _, _, handler) = await SetupAsync(db, timeoutSeconds: -10);
    suoritin.Hang = true;

    var result = await handler.HandleAsync(new HandlerContext(e, /*lang=json,strict*/ """{"source":"x"}""", DateTimeOffset.UtcNow));

    Assert.Equal("timeout", result.Status);
  }

  [Fact]
  public async Task Pre_cancelled_token_propagates_cancellation()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, _, handler) = await SetupAsync(db);
    suoritin.Hang = true;
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(
      () => handler.HandleAsync(new HandlerContext(e, /*lang=json,strict*/ """{"source":"x"}""", DateTimeOffset.UtcNow), cts.Token));
  }
}
