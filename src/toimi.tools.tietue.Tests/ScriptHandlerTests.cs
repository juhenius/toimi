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

  private static ScriptHandler Handler()
  {
    var entities = new EntityRepository(TestDb.New(), new SchemaValidator());
    return new ScriptHandler(
      new FakeSuoritinClient(), new ScriptEffectApplier(entities, new FakeMcpInvoker()), new RunTokenStore(),
      new ScriptOptions(), new SuoritinOptions());
  }

  private static async Task<(Data.Entity e, FakeSuoritinClient suoritin, FakeMcpInvoker mcp, RunTokenStore tokens, ScriptHandler handler)> SetupAsync(
    Data.TietueDbContext db, string entityJson = /*lang=json,strict*/ """{"name":"Jari","status":"open"}""", bool enabled = true, ScriptBudget? budget = null)
  {
    await new TypeRepository(db).DefineAsync("task", Schema);
    var entities = new EntityRepository(db, new SchemaValidator());
    var e = await entities.CreateAsync("task", JsonNode.Parse(entityJson), []);
    var suoritin = new FakeSuoritinClient();
    var mcp = new FakeMcpInvoker();
    var tokens = new RunTokenStore();
    var handler = new ScriptHandler(
      suoritin, new ScriptEffectApplier(entities, mcp), tokens,
      new ScriptOptions { Enabled = enabled }, new SuoritinOptions(), budget);
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
    Assert.Equal(["api.example.com"], request.Net);
    Assert.Null(request.Extract);
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
    Assert.Equal(["a.example"], request.Net);
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
  public async Task Input_params_default_to_empty_object()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, _, handler) = await SetupAsync(db);
    var config = /*lang=json,strict*/ """{"source":"export default () => ({})","capabilities":[]}""";

    await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));

    var input = Assert.Single(suoritin.Requests).Input;
    Assert.Equal(System.Text.Json.JsonValueKind.Object, input.GetProperty("params").ValueKind);
    Assert.Equal(0, input.GetProperty("params").GetPropertyCount());
  }

  [Fact]
  public async Task Input_params_carry_the_firing_arguments()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, _, handler) = await SetupAsync(db);
    var config = /*lang=json,strict*/ """{"source":"export default () => ({})","capabilities":[]}""";
    using var doc = System.Text.Json.JsonDocument.Parse(/*lang=json,strict*/ """{"door":"front","count":2}""");

    await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow, doc.RootElement.Clone()));

    var input = Assert.Single(suoritin.Requests).Input;
    Assert.Equal("front", input.GetProperty("params").GetProperty("door").GetString());
    Assert.Equal(2, input.GetProperty("params").GetProperty("count").GetInt32());
  }

  [Fact]
  public async Task Llm_grant_ships_extract_and_widens_net_to_the_callback_host()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, tokens, handler) = await SetupAsync(db);
    var config = /*lang=json,strict*/ """{"source":"export default () => ({})","capabilities":["llm"],"allowedHosts":["api.example.com"]}""";

    await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));

    var request = Assert.Single(suoritin.Requests);
    Assert.NotNull(request.Extract);
    // Full URL composed here: the sandbox never learns the route shape.
    Assert.Equal(
      ExtractEndpoints.CallbackUrl(new SuoritinOptions().CallbackBaseUrl),
      request.Extract.Url);
    // net = allowedHosts + callback host, and nothing else.
    Assert.Equal(["api.example.com", "toimi-tools-tietue.apps.svc.cluster.local"], request.Net);
    Assert.False(tokens.TryUseExtract(request.Extract.Token)); // revoked after the run
  }

  [Fact]
  public async Task No_llm_grant_means_no_extract_and_no_net_widening()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, _, handler) = await SetupAsync(db);
    var config = /*lang=json,strict*/ """{"source":"export default () => ({})","capabilities":["setField"],"allowedHosts":["api.example.com"]}""";

    await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));

    var request = Assert.Single(suoritin.Requests);
    Assert.Null(request.Extract);
    Assert.Equal(["api.example.com"], request.Net);
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
    // A genuinely tiny but valid budget: the watchdog fires in ~40ms instead of
    // stalling the test for the production 30s (was: a timeoutSeconds:-10 hack).
    var tiny = new ScriptBudget(TimeSpan.FromMilliseconds(40), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(60));
    var (e, suoritin, _, _, handler) = await SetupAsync(db, budget: tiny);
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

  [Theory]
  [InlineData(null)]
  [InlineData("{}")]
  [InlineData(/*lang=json,strict*/ """{"fromEntity":false}""")]
  [InlineData(/*lang=json,strict*/ """{"source":""}""")]
  public void ValidateConfig_rejects_configs_with_nothing_to_execute(string? config)
  {
    Assert.False(Handler().ValidateConfig(config).IsValid);
  }

  [Fact]
  public void ValidateConfig_rejects_non_array_hosts_and_capabilities()
  {
    // StrArray silently coerces wrong shapes to [] — a string-valued allowedHosts becomes
    // a script with no egress.
    var result = Handler().ValidateConfig(/*lang=json,strict*/ """{"source":"export default () => ({})","allowedHosts":"api.example.com","capabilities":[1]}""");
    Assert.False(result.IsValid);
    Assert.Equal(2, result.Errors.Count);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"fromEntity":true}""")]
  [InlineData(/*lang=json,strict*/ """{"source":"export default () => ({})"}""")]
  [InlineData(/*lang=json,strict*/ """{"source":"export default () => ({})","allowedHosts":["api.example.com"],"capabilities":["setField"]}""")]
  public void ValidateConfig_accepts_runnable_configs(string config)
  {
    Assert.True(Handler().ValidateConfig(config).IsValid);
  }
}
