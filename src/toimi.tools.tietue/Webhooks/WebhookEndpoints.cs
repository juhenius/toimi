using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Webhooks;

/// <summary>
/// The /hooks/{triggerId}/{secret} capability-URL endpoint (ADR 0001). Everything
/// pre-auth — unknown id, wrong secret, disabled, kill-switched, outside the validity
/// window, non-webhook anchor — is the same bare 404, so the endpoint never confirms a
/// webhook's existence. Post-auth diagnostics are honest: 429 rate limit, 413 body cap,
/// 400 malformed JSON. A valid call is a doorbell: 202 + occurrence id, handler runs
/// in the background, handler output never reaches the caller.
/// </summary>
public static class WebhookEndpoints
{
  public const string Base = Toimi.Core.Webhooks.HookRoute.Base;

  /// <summary>The full capability URL for a webhook trigger; null for time anchors or when PublicBaseUrl is unset.</summary>
  public static string? Url(WebhookOptions options, Trigger trigger)
  {
    return trigger.Secret is null || string.IsNullOrEmpty(options.PublicBaseUrl)
      ? null
      : $"{options.PublicBaseUrl.TrimEnd('/')}{Base}/{trigger.Id}/{trigger.Secret}";
  }

  /// <summary>
  /// Adds the capability fields (url + secret) to a trigger tool-response row when the
  /// trigger is call-anchored. The one shaping site shared by set/update/list, so a
  /// format or redaction change cannot silently drift between them. The secret rides
  /// along even when no PublicBaseUrl composes a url (ADR 0001: "retrievable through
  /// the trigger tools") — without it, a lost creation response would make the
  /// capability URL unrecoverable.
  /// </summary>
  public static void AddCapabilityFields(JsonObject row, WebhookOptions options, Trigger trigger)
  {
    if (trigger.Secret is null)
    {
      return;
    }

    row["url"] = Url(options, trigger);
    row["secret"] = trigger.Secret;
  }

  public static void MapWebhookEndpoints(WebApplication app)
  {
    app.MapMethods($"{Base}/{{triggerId:guid}}/{{secret}}", ["GET", "POST"], (
      Guid triggerId,
      string secret,
      HttpRequest request,
      TietueDbContext db,
      WebhookOptions options,
      WebhookRateLimiter limiter,
      WebhookDispatchChannel queue,
      CancellationToken ct) => HandleAsync(triggerId, secret, request, db, options, limiter, queue, DateTimeOffset.UtcNow, ct));
  }

  public static async Task<IResult> HandleAsync(
    Guid triggerId, string secret, HttpRequest request, TietueDbContext db,
    WebhookOptions options, WebhookRateLimiter limiter, WebhookDispatchChannel queue,
    DateTimeOffset now, CancellationToken ct)
  {
    if (!options.Enabled)
    {
      return Results.NotFound();
    }

    // Global meter (sentinel key Guid.Empty — gen_random_uuid() never mints it): charged
    // ONLY by requests that fail authentication, so a secretless probe stream can never
    // starve legitimate capability calls — those skip the global bucket entirely and are
    // governed by their per-webhook limit alone. The accepted price: every probe costs
    // one indexed PK lookup (trivial at any realistic rate; this deployment is LAN-only).
    // Over-cap probes get 429, which reveals only that /hooks exists — already public.
    var trigger = await db.Triggers.FirstOrDefaultAsync(t => t.Id == triggerId, ct);
    if (trigger?.Secret is null || !FixedTimeEquals(trigger.Secret, secret))
    {
      return limiter.TryAcquire(Guid.Empty, options.GlobalRateLimitPerMinute)
        ? Results.NotFound()
        : Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    var spec = Schedule.Parse(trigger.Schedule)?.Webhook;
    if (spec is null || !trigger.Enabled
      || (spec.ActiveAfter is { } after && now < after)
      || (spec.ActiveUntil is { } until && now >= until))
    {
      return Results.NotFound();
    }

    if (!limiter.TryAcquire(trigger.Id, spec.RateLimit ?? options.RateLimitPerMinute))
    {
      return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    if (request.ContentLength > options.MaxBodyBytes)
    {
      return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    var body = await ReadCappedAsync(request.Body, options.MaxBodyBytes, ct);
    if (body is null)
    {
      return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    JsonElement @params;
    try
    {
      @params = MergeParams(request.Query, body);
    }
    catch (JsonException)
    {
      return Results.BadRequest(new { error = "Request body must be a JSON object." });
    }

    var occurrence = now;
    // Accepted micro-race: two calls in the same clock instant collide on the
    // (entity, occurrence, kind) claim and the later one is dropped by the dispatcher.
    return queue.TryEnqueue(new WebhookFiring(trigger.Id, occurrence, @params))
      ? Results.Accepted(null, new WebhookAccepted(occurrence.ToString("o")))
      : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
  }

  /// <summary>The whole 202 body: the occurrence identity this call was accepted as — never handler output.</summary>
  public sealed record WebhookAccepted(string Occurrence);

  private static bool FixedTimeEquals(string stored, string presented)
  {
    return CryptographicOperations.FixedTimeEquals(
      Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(presented));
  }

  /// <summary>Null when the stream exceeds the cap — Content-Length is caller-asserted, chunked bodies have none.</summary>
  private static async Task<byte[]?> ReadCappedAsync(Stream body, int maxBytes, CancellationToken ct)
  {
    using var buffer = new MemoryStream();
    var chunk = new byte[8192];
    int read;
    while ((read = await body.ReadAsync(chunk, ct)) > 0)
    {
      if (buffer.Length + read > maxBytes)
      {
        return null;
      }

      buffer.Write(chunk, 0, read);
    }

    return buffer.ToArray();
  }

  /// <summary>
  /// Params = query string overlaid by the JSON body, body wins per key (the deliberate
  /// interface beats the dumb-caller fallback). Query values are strings, last value per
  /// key; no type coercion. Throws JsonException when the body is malformed OR its root
  /// is not an object — one failure signal, one 400 path.
  /// </summary>
  private static JsonElement MergeParams(IQueryCollection query, byte[] body)
  {
    var merged = new JsonObject();
    foreach (var (key, values) in query)
    {
      merged[key] = values.LastOrDefault() ?? "";
    }

    if (body.Length > 0)
    {
      using var doc = JsonDocument.Parse(body);
      if (doc.RootElement.ValueKind != JsonValueKind.Object)
      {
        throw new JsonException("Request body root is not a JSON object.");
      }

      foreach (var property in doc.RootElement.EnumerateObject())
      {
        merged[property.Name] = JsonSerializer.SerializeToNode(property.Value);
      }
    }

    return JsonSerializer.SerializeToElement(merged);
  }
}
