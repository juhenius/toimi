using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Toimi.Core.Webhooks;
using toimi.tools.ruutu.Data;
using toimi.tools.ruutu.Data.Entities;
using toimi.tools.ruutu.Rendering;

namespace toimi.tools.ruutu.Transport;

/// <summary>
/// Forwards a wired display event to its webhook capability URL (ADR 0002):
/// the event becomes the firing's params, the outcome lands on the event row,
/// and a failure is surfaced on the display as a notification overlay —
/// ruutu's own voice, plumbing feedback only. Runs off the request path (see
/// ActionForwardWorker) and never throws except on shutdown cancellation.
///
/// Outcomes are a closed vocabulary the seeded skills teach agents to read:
/// "ok" | "error: &lt;status&gt;" | "error: timeout" | "error: unreachable" |
/// "error: failed".
/// </summary>
public class ActionForwarder(
  IHttpClientFactory httpClientFactory,
  ContentPushService pusher,
  RuutuDbContext db,
  IOptions<ActionOptions> options,
  ILogger<ActionForwarder> logger)
{
  public const string HttpClientName = "actions";

  private const string HookPathPrefix = $"{HookRoute.Base}/";
  private const string FailureDataJson = /*lang=json,strict*/
    """{"severity":"warn","title":"Action failed","body":"Couldn't reach toimi — that tap wasn't delivered."}""";

  public async Task ForwardAsync(ActionForward forward, CancellationToken ct = default)
  {
    var evt = await db.DisplayEvents.FirstOrDefaultAsync(e => e.Id == forward.EventId, ct);
    if (evt is null)
    {
      logger.LogWarning("Action forward skipped: display event {EventId} is gone.", forward.EventId);
      return;
    }

    var targetUrl = RewriteForCluster(forward.Url, options.Value.PublicHookHost, options.Value.InternalHookBase);
    WarnOnHostDrift(forward.Url, targetUrl);

    string outcome;
    try
    {
      var client = httpClientFactory.CreateClient(HttpClientName);
      using var content = new StringContent(BuildParams(forward.Identifier, evt), Encoding.UTF8, "application/json");
      using var response = await client.PostAsync(targetUrl, content, ct);
      outcome = response.IsSuccessStatusCode ? "ok" : $"error: {(int)response.StatusCode}";
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      throw; // shutdown — the worker loop handles it
    }
    catch (TaskCanceledException)
    {
      outcome = "error: timeout"; // the named client's timeout, not our token
    }
    catch (HttpRequestException)
    {
      outcome = "error: unreachable";
    }
#pragma warning disable CA1031 // Forwarding must never escape the worker; every failure becomes an outcome + overlay.
    catch (Exception ex)
#pragma warning restore CA1031
    {
      logger.LogWarning(ex, "Action forward for display '{Identifier}' failed with an unexpected error.", forward.Identifier);
      outcome = "error: failed";
    }

    try
    {
      evt.ForwardOutcome = outcome;
      await db.SaveChangesAsync(ct);
    }
#pragma warning disable CA1031 // A DB hiccup recording the outcome must not suppress the failure overlay or escape.
    catch (Exception ex)
#pragma warning restore CA1031
    {
      logger.LogWarning(ex, "Failed to record forward outcome for display event {EventId}.", forward.EventId);
    }

    if (outcome != "ok")
    {
      logger.LogWarning("Forwarding '{Type}' event for display '{Identifier}' failed: {Outcome}",
        evt.EventType, forward.Identifier, outcome);
      await PushFailureOverlayAsync(forward.Identifier, ct);
    }
  }

  /// <summary>
  /// Rewrites a public /hooks capability URL onto the cluster-internal
  /// service (see ActionOptions). Anything else — other hosts, other paths,
  /// unset config, unparseable URL — passes through verbatim.
  /// </summary>
  public static string RewriteForCluster(string url, string? publicHookHost, string? internalHookBase)
  {
    return string.IsNullOrEmpty(publicHookHost)
      || string.IsNullOrEmpty(internalHookBase)
      || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
      || !string.Equals(uri.Host, publicHookHost, StringComparison.OrdinalIgnoreCase)
      || !uri.AbsolutePath.StartsWith(HookPathPrefix, StringComparison.Ordinal)
      ? url
      : $"{internalHookBase.TrimEnd('/')}{uri.PathAndQuery}";
  }

  /// <summary>
  /// A /hooks URL whose host doesn't match the configured public host is
  /// forwarded verbatim — which in-cluster usually means a TLS failure. That
  /// combination is config drift (TOIMI_HOST changed, stale stored actions),
  /// so make it loud instead of letting the rewrite go silently inert.
  /// </summary>
  private void WarnOnHostDrift(string url, string targetUrl)
  {
    if (targetUrl == url
      && !string.IsNullOrEmpty(options.Value.PublicHookHost)
      && !string.IsNullOrEmpty(options.Value.InternalHookBase)
      && Uri.TryCreate(url, UriKind.Absolute, out var uri)
      && uri.AbsolutePath.StartsWith(HookPathPrefix, StringComparison.Ordinal))
    {
      logger.LogWarning(
        "Forwarding a /hooks URL for host '{Host}' that does not match Actions__PublicHookHost '{Expected}' — not rewritten to the cluster-internal service; check for config drift against tietue's Webhooks__PublicBaseUrl or stale stored actions.",
        uri.Host, options.Value.PublicHookHost);
    }
  }

  private static string BuildParams(string identifier, DisplayEvent evt)
  {
    var node = new JsonObject
    {
      ["type"] = evt.EventType,
      ["target"] = evt.Target,
      ["value"] = evt.Value is null ? null : JsonNode.Parse(evt.Value),
      ["display"] = identifier,
    };
    return node.ToJsonString();
  }

  // Semantic compare — stored frame data is re-serialized (unicode-escaped), so
  // raw string equality against FailureDataJson would never match.
  private static bool IsFailureData(string dataJson)
  {
    try
    {
      using var doc = JsonDocument.Parse(dataJson);
      return doc.RootElement.TryGetProperty("title", out var title)
        && title.ValueKind == JsonValueKind.String
        && title.GetString() == "Action failed";
    }
    catch (JsonException)
    {
      return false;
    }
  }

  private async Task PushFailureOverlayAsync(string identifier, CancellationToken ct)
  {
    try
    {
      // Dedupe: the user's finger is the retry loop — repeated failed taps must
      // not stack identical cards the user then dismisses one by one.
      var display = await db.Displays.FirstOrDefaultAsync(d => d.Identifier == identifier, ct);
      var stack = OverlayStack.Parse(display?.OverlayStack ?? "[]");
      if (stack.Length > 0 && stack[0].Template == "notification" && IsFailureData(stack[0].DataJson))
      {
        return;
      }

      using var data = JsonDocument.Parse(FailureDataJson);
      await pusher.ShowOverlayAsync(identifier, "notification", data.RootElement, ct);
    }
#pragma warning disable CA1031 // The overlay is best-effort feedback; a render/push failure must not escape the worker.
    catch (Exception ex)
#pragma warning restore CA1031
    {
      logger.LogWarning(ex, "Failed to push action-failure overlay for '{Identifier}'", identifier);
    }
  }
}
