using System.Text.Json;
using toimi.tools.tietue.Scripts;

namespace toimi.tools.tietue.Handlers;

public class ScriptHandler(ScriptEngine engine, ScriptEffectApplier applier, ScriptOptions options) : INativeHandler
{
  public string Kind => "script";

  public async Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default)
  {
    if (!options.Enabled)
    {
      return new HandlerResult("disabled");
    }

    string source = "", capabilitiesRaw = "[]";
    if (ctx.ConfigJson is not null)
    {
      using var cfg = JsonDocument.Parse(ctx.ConfigJson);
      if (cfg.RootElement.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String)
      {
        source = s.GetString() ?? "";
      }

      if (cfg.RootElement.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Array)
      {
        capabilitiesRaw = c.GetRawText();
      }
    }

    var capabilities = JsonSerializer.Deserialize<string[]>(capabilitiesRaw) ?? [];
    var effectsJson = await EvaluateWithWatchdogAsync(source, ctx.Entity.Data.RootElement.GetRawText(), ct);
    if (effectsJson is null)
    {
      return new HandlerResult("timeout", /*lang=json,strict*/ """{"error":"script exceeded its wall-clock budget"}""");
    }

    var effects = ScriptEffects.Parse(effectsJson);
    var applied = await applier.ApplyAsync(ctx.Entity, effects, capabilities, ct);

    return new HandlerResult("ran", JsonSerializer.Serialize(new { applied }));
  }

  /// <summary>
  /// Evaluates on a pool thread and stops WAITING at the configured budget. Jint's own
  /// limits are cooperative, so a single atomic native call (a dynamically-built
  /// catastrophic regex, a large allocation) can outrun them. The scheduler tick holds
  /// the Postgres advisory tick lock while a handler runs, so an unbounded script stalls
  /// every replica's scheduler — this bounds that to the budget.
  ///
  /// .NET cannot abort the abandoned thread: it keeps burning a thread-pool thread until
  /// Jint's internal caps end it. What this guarantees is that the TICK moves on, not that
  /// the runaway stops immediately.
  ///
  /// Deliberate: passing `ct` to WaitAsync makes script evaluation cancellation-aware for
  /// the first time — a shutdown mid-script now surfaces as an OperationCanceledException
  /// out of HandleAsync (SchedulerTick records an error event and advances) instead of the
  /// script silently running to completion. Shutdown should not wait on untrusted scripts.
  /// </summary>
  private async Task<string?> EvaluateWithWatchdogAsync(string source, string dataJson, CancellationToken ct)
  {
    try
    {
      return await Task.Run(() => engine.Evaluate(source, dataJson), ct)
        .WaitAsync(TimeSpan.FromSeconds(options.TimeoutSeconds), ct);
    }
    catch (TimeoutException)
    {
      return null;
    }
  }
}
