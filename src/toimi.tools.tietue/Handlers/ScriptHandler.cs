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
    var effectsJson = engine.Evaluate(source, ctx.Entity.Data.RootElement.GetRawText());
    var effects = ScriptEffects.Parse(effectsJson);
    var applied = await applier.ApplyAsync(ctx.Entity, effects, capabilities, ct);

    return new HandlerResult("ran", JsonSerializer.Serialize(new { applied }));
  }
}
