using System.Text.Json;
using toimi.tools.tietue.Scripts;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Handlers;

public class ScriptHandler(
  ISuoritinClient suoritin,
  ScriptEffectApplier applier,
  RunTokenStore tokens,
  ScriptOptions options,
  SuoritinOptions suoritinOptions) : INativeHandler
{
  public string Kind => "script";

  /// <summary>
  /// The seeded job type's name. Shared with <c>TypeSeeder</c> so a rename of the
  /// seeded type cannot silently detach the reserved-field hardening below.
  /// </summary>
  public const string JobTypeName = "job";

  /// <summary>
  /// Job-entity control fields a script's own setField effects may never write:
  /// rewriting them would grant the (untrusted) script arbitrary code, grants,
  /// or egress on its next scheduled run.
  /// </summary>
  public static readonly string[] ReservedJobFields = ["code", "grants", "allowedHosts", "enabled"];

  private sealed record ResolvedScript(string Source, string[] AllowedHosts, string[] Grants, bool Enabled, bool FromEntity);

  public async Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default)
  {
    if (!options.Enabled)
    {
      return new HandlerResult("disabled");
    }

    var script = Resolve(ctx);
    if (script is null)
    {
      return new HandlerResult("error", /*lang=json,strict*/ """{"error":"no script source configured"}""");
    }

    if (!script.Enabled)
    {
      return new HandlerResult("disabled", /*lang=json,strict*/ """{"reason":"job entity has enabled:false"}""");
    }

    string? token = null;
    if (script.Grants.Contains("llm", StringComparer.OrdinalIgnoreCase))
    {
      token = tokens.Issue(ctx.Entity.Id, script.Grants, TimeSpan.FromSeconds(options.TimeoutSeconds + 30));
    }

    var request = new SuoritinRequest(
      script.Source,
      BuildInput(ctx),
      options.TimeoutSeconds * 1000,
      script.AllowedHosts,
      script.Grants,
      token,
      token is null ? null : suoritinOptions.CallbackBaseUrl);

    SuoritinResult run;
    try
    {
      // Outer watchdog: the scheduler tick holds the advisory tick lock while a
      // handler runs, so even a hung suoritin connection must be bounded.
      run = await suoritin.ExecuteAsync(request, ct)
        .WaitAsync(TimeSpan.FromSeconds(options.TimeoutSeconds + 10), ct);
    }
    catch (TimeoutException)
    {
      return new HandlerResult("timeout", /*lang=json,strict*/ """{"error":"suoritin did not respond within the watchdog budget"}""");
    }
    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
    {
      return new HandlerResult("timeout", /*lang=json,strict*/ """{"error":"suoritin request timed out (HTTP client timeout)"}""");
    }
    catch (HttpRequestException ex)
    {
      return new HandlerResult("error", JsonSerializer.Serialize(new { error = $"suoritin unreachable: {ex.Message}" }));
    }
    finally
    {
      if (token is not null)
      {
        tokens.Revoke(token);
      }
    }

    if (!run.Ok)
    {
      // run.Error is untrusted suoritin output; cap it like the log lines.
      return new HandlerResult("error", JsonSerializer.Serialize(new
      {
        error = run.Error is null ? null : SuoritinClient.Truncate(run.Error),
        logs = run.Logs,
      }));
    }

    var effects = ScriptEffects.Parse(run.EffectsJson ?? "{}");
    // Control fields are reserved for any job entity, not just fromEntity runs:
    // an inline trigger script attached to a job must not rewrite the code,
    // grants, or egress its next scheduled (fromEntity) run will execute with.
    var reserved = script.FromEntity || ctx.Entity.Type == JobTypeName ? ReservedJobFields : [];
    var applied = await applier.ApplyAsync(
      ctx.Entity, effects, script.Grants, reserved,
      TimeSpan.FromSeconds(options.EffectsTimeoutSeconds), ct);
    return new HandlerResult("ran", JsonSerializer.Serialize(new { applied, logs = run.Logs, durationMs = run.DurationMs }));
  }

  private static ResolvedScript? Resolve(HandlerContext ctx)
  {
    var fromEntity = false;
    string? source = null;
    string[] hosts = [], grants = [];

    if (ctx.ConfigJson is not null)
    {
      using var cfg = JsonDocument.Parse(ctx.ConfigJson);
      // fromEntity:true means the job entity is authoritative: any inline
      // source/capabilities/allowedHosts in the trigger config are ignored.
      fromEntity = cfg.RootElement.TryGetProperty("fromEntity", out var fe) && fe.ValueKind == JsonValueKind.True;
      if (!fromEntity)
      {
        source = Str(cfg.RootElement, "source");
        hosts = StrArray(cfg.RootElement, "allowedHosts");
        grants = StrArray(cfg.RootElement, "capabilities");
      }
    }

    var enabled = true;
    if (fromEntity)
    {
      var data = ctx.Entity.Data.RootElement;
      source = Str(data, "code");
      hosts = StrArray(data, "allowedHosts");
      grants = StrArray(data, "grants");
      enabled = !(data.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False);
    }

    return string.IsNullOrWhiteSpace(source) ? null : new ResolvedScript(source, hosts, grants, enabled, fromEntity);
  }

  private static JsonElement BuildInput(HandlerContext ctx)
  {
    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
      data = ctx.Entity.Data.RootElement,
      entityId = ctx.Entity.Id.ToString(),
      entityType = ctx.Entity.Type,
      occurrence = ctx.OccurrenceUtc.ToString("o"),
    }));
    return doc.RootElement.Clone();
  }

  private static string? Str(JsonElement e, string name)
  {
    return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
  }

  private static string[] StrArray(JsonElement e, string name)
  {
    return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
      ? [.. v.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!)]
      : [];
  }

  public ValidationResult ValidateConfig(string? configJson)
  {
    const string Requirement = "script config requires 'source' as a non-empty string, or 'fromEntity': true to run the job entity's own code.";
    using var cfg = ConfigValidation.RequireObject(configJson, Requirement, out var failure);
    if (cfg is null)
    {
      return failure!;
    }

    var root = cfg.RootElement;
    if (root.TryGetProperty("fromEntity", out var fe) && fe.ValueKind == JsonValueKind.True)
    {
      return ValidationResult.Valid(); // the job entity is authoritative; inline fields are ignored
    }

    var errors = new List<string>();
    if (string.IsNullOrWhiteSpace(Str(root, "source")))
    {
      errors.Add(Requirement);
    }

    foreach (var name in (string[])["allowedHosts", "capabilities"])
    {
      if (root.TryGetProperty(name, out var v)
        && (v.ValueKind != JsonValueKind.Array || v.EnumerateArray().Any(i => i.ValueKind != JsonValueKind.String)))
      {
        errors.Add($"script config '{name}' must be an array of strings.");
      }
    }

    return errors.Count == 0 ? ValidationResult.Valid() : ValidationResult.Invalid(errors);
  }
}
