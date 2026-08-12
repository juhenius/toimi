using System.Text;
using System.Text.Json;

namespace toimi.tools.tietue.Scripts;

public class SuoritinOptions
{
  public string BaseUrl { get; set; } = "http://toimi-tools-suoritin.apps.svc.cluster.local";

  /// <summary>Base URL suoritin's workers use to reach tietue's extract() callback.</summary>
  public string CallbackBaseUrl { get; set; } = "http://toimi-tools-tietue.apps.svc.cluster.local";
}

public record ExtractGrant(string Url, string Token);

public record SuoritinRequest(
  string Code,
  JsonElement Input,
  int TimeoutMs,
  string[] Net,
  ExtractGrant? Extract);

public record SuoritinResult(bool Ok, string? EffectsJson, string[] Logs, string? Error, long DurationMs);

public interface ISuoritinClient
{
  /// <summary>Executes a script on the suoritin pod. Throws HttpRequestException/TaskCanceledException on transport failure.</summary>
  Task<SuoritinResult> ExecuteAsync(SuoritinRequest request, CancellationToken ct = default);
}

public class SuoritinClient(IHttpClientFactory httpFactory) : ISuoritinClient
{
  public const string HttpClientName = "suoritin";

  // tietue-side caps on suoritin's untrusted output (spec §4/§7); the named
  // HTTP client additionally caps the whole response body at 1 MB.
  // Counterparts (keep equal — a shared artifact across C#/TS isn't feasible,
  // paired comments + SuoritinIntegrationTests are the discipline):
  //   MaxLogEntries  = MAX_LOGS        (suoritin limits.ts; sandbox truncates
  //                                     first, this Take() is a pure backstop)
  //   MaxLogChars    = MAX_LOG_CHARS   (limits.ts; also reused for error
  //                                     strings, = MAX_ERROR_CHARS)
  //   MaxEffectsBytes = MAX_EFFECTS_BYTES (suoritin executor.ts)
  public const int MaxLogEntries = 100;
  public const int MaxLogChars = 2000;
  public const int MaxEffectsBytes = 256 * 1024;

  // WhenWritingNull: an absent extract must be omitted, not sent as JSON null —
  // counterpart: suoritin main.ts validateOptionalFields (null tolerated as
  // absent, malformed non-null rejected).
  private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web)
  {
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
  };

  public async Task<SuoritinResult> ExecuteAsync(SuoritinRequest request, CancellationToken ct = default)
  {
    var client = httpFactory.CreateClient(HttpClientName);
    var payload = new
    {
      code = request.Code,
      input = request.Input,
      timeoutMs = request.TimeoutMs,
      net = request.Net,
      extract = request.Extract,
    };
    using var response = await client.PostAsJsonAsync("/execute", payload, CamelCase, ct);
    response.EnsureSuccessStatusCode();

    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    var root = doc.RootElement;
    var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
    var logs = ParseLogs(root);
    var error = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString() : null;
    var durationMs = root.TryGetProperty("stats", out var stats) && stats.TryGetProperty("durationMs", out var d) && d.TryGetInt64(out var ms) ? ms : 0;
    var effectsJson = root.TryGetProperty("effects", out var eff) && eff.ValueKind == JsonValueKind.Object ? eff.GetRawText() : null;

    return effectsJson is not null && Encoding.UTF8.GetByteCount(effectsJson) > MaxEffectsBytes
      ? new SuoritinResult(false, null, logs, "effects payload exceeds tietue-side cap", durationMs)
      : new SuoritinResult(ok, effectsJson, logs, error, durationMs);
  }

  /// <summary>Caps a single untrusted suoritin string (log line, error message) at <see cref="MaxLogChars"/>.</summary>
  public static string Truncate(string value)
  {
    return value.Length > MaxLogChars ? value[..MaxLogChars] + "…" : value;
  }

  private static string[] ParseLogs(JsonElement root)
  {
    return root.TryGetProperty("logs", out var logs) && logs.ValueKind == JsonValueKind.Array
      ? [.. logs.EnumerateArray()
          .Where(l => l.ValueKind == JsonValueKind.String)
          .Take(MaxLogEntries)
          .Select(l => l.GetString()!)
          .Select(Truncate)]
      : [];
  }
}
