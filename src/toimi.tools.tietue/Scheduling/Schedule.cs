using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ical.Net.DataTypes;

namespace toimi.tools.tietue.Scheduling;

/// <summary>
/// The single owner of the trigger anchor grammar: one-shot {"at":"&lt;iso utc&gt;"}, recurring
/// {"start":"&lt;iso utc&gt;","rrule":"FREQ=...","tz":"&lt;iana&gt;"}, or call-anchored
/// {"webhook":{"activeAfter"?,"activeUntil"?,"rateLimit"?}}. Every instance is grammatically
/// parseable by construction (Parse or the typed factories); TryValidate covers the semantic
/// rules writers enforce, including that a schedule has exactly one anchor. ToJson returns
/// exactly the JSON the schedule was built from (plus any stamped tz), so persisted schedules
/// stay compatible with what callers wrote.
/// </summary>
public sealed class Schedule
{
  private readonly string _json;

  private Schedule(string json, DateTimeOffset? at, DateTimeOffset? start, string? rrule, string? tz, WebhookSpec? webhook)
  {
    _json = json;
    At = at;
    Start = start;
    Rrule = rrule;
    Tz = tz;
    Webhook = webhook;
  }

  public DateTimeOffset? At { get; }
  public DateTimeOffset? Start { get; }
  public string? Rrule { get; }
  public string? Tz { get; }
  public WebhookSpec? Webhook { get; }

  /// <summary>A spec with 'at' is one-shot even when rrule fields are also present ('at' wins in NextOnOrAfter/NextAfter).</summary>
  public bool IsRecurring => At is null && Start is not null && Rrule is not null;

  /// <summary>Call-anchored: fired by inbound HTTP calls to the trigger's endpoint, never by the clock.</summary>
  public bool IsWebhook => Webhook is not null;

  /// <summary>Null when the JSON is not an object or a date field doesn't parse — the grammar is unmet.</summary>
  public static Schedule? Parse(string json)
  {
    try
    {
      using var doc = JsonDocument.Parse(json);
      var root = doc.RootElement;
      return root.ValueKind != JsonValueKind.Object
        ? null
        : new Schedule(json, Time(root, "at"), Time(root, "start"), Str(root, "rrule"), Str(root, "tz"), ParseWebhook(root));
    }
    catch (Exception ex) when (ex is JsonException or FormatException)
    {
      return null;
    }
  }

  public static Schedule OneShotAt(DateTimeOffset at)
  {
    var utc = at.ToUniversalTime();
    return new Schedule(new JsonObject { ["at"] = utc.ToString("o") }.ToJsonString(), utc, null, null, null, null);
  }

  public static Schedule Recurring(DateTimeOffset start, string rrule, string? tz = null)
  {
    var utc = start.ToUniversalTime();
    var node = new JsonObject { ["start"] = utc.ToString("o"), ["rrule"] = rrule };
    if (tz is not null)
    {
      node["tz"] = tz;
    }

    return new Schedule(node.ToJsonString(), null, utc, rrule, tz, null);
  }

  public static Schedule ForWebhook(DateTimeOffset? activeAfter = null, DateTimeOffset? activeUntil = null, int? rateLimit = null)
  {
    var spec = new JsonObject();
    if (activeAfter is { } after)
    {
      spec["activeAfter"] = after.ToUniversalTime().ToString("o");
    }

    if (activeUntil is { } until)
    {
      spec["activeUntil"] = until.ToUniversalTime().ToString("o");
    }

    if (rateLimit is { } limit)
    {
      spec["rateLimit"] = limit;
    }

    var json = new JsonObject { ["webhook"] = spec }.ToJsonString();
    return new Schedule(json, null, null, null, null,
      new WebhookSpec(activeAfter?.ToUniversalTime(), activeUntil?.ToUniversalTime(), rateLimit));
  }

  /// <summary>A schedule with the default tz stamped onto a recurring spec that omits one; otherwise this instance.</summary>
  public Schedule WithDefaultTz(string defaultTz)
  {
    if (Rrule is null || !string.IsNullOrEmpty(Tz))
    {
      return this; // one-shot or already zoned
    }

    var node = JsonNode.Parse(_json)!.AsObject();
    node["tz"] = defaultTz;
    return new Schedule(node.ToJsonString(), At, Start, Rrule, defaultTz, Webhook);
  }

  /// <summary>
  /// The provably-invalid checks, independent of the clock: grammar, rrule syntax, and the
  /// sub-daily+BY-parts+DST-tz combination the calculator refuses. Deliberately does NOT
  /// reject elapsed one-shots or exhausted recurrences — that distinction is the caller's,
  /// via NextOnOrAfter (see TriggerRepository).
  /// </summary>
  public bool TryValidate(out string? error)
  {
    if (IsWebhook)
    {
      if (At is not null || Start is not null || Rrule is not null)
      {
        error = "A schedule has exactly one anchor: 'at' (one-shot), 'start'+'rrule' (recurring), or 'webhook' (call-anchored).";
        return false;
      }

      if (Webhook is { ActiveAfter: { } after, ActiveUntil: { } until } && after >= until)
      {
        error = "Webhook 'activeAfter' must be earlier than 'activeUntil'.";
        return false;
      }

      if (Webhook is { RateLimit: < 1 })
      {
        error = "Webhook 'rateLimit' must be a positive integer (firings per minute).";
        return false;
      }

      error = null;
      return true;
    }

    if (At is null && !IsRecurring)
    {
      error = "Schedule must be {\"at\":\"<iso utc>\"} (one-shot), {\"start\":\"<iso utc>\",\"rrule\":\"FREQ=...\"} (recurring), or {\"webhook\":{...}} (call-anchored).";
      return false;
    }

    if (IsRecurring)
    {
      try
      {
        _ = new RecurrencePattern(Rrule!);
      }
      catch (Exception ex) when (ex is ArgumentException or FormatException)
      {
        error = $"Invalid rrule '{Rrule}': {ex.Message}";
        return false;
      }
    }

    // Keyed on Rrule presence (not IsRecurring) to match the old HasUnsupportedSubDailyRule:
    // set_trigger has always rejected this combination even alongside an 'at'.
    if (Rrule is { } rrule && RecurrenceCalculator.IsUnsupportedSubDaily(rrule, Tz))
    {
      error = "Sub-daily rules (SECONDLY/MINUTELY/HOURLY) with BY-part filters are not supported in DST timezones; "
        + "use plain INTERVAL form, or FREQ=DAILY with BYHOUR/BYMINUTE for wall-clock times, or pass tz:\"UTC\".";
      return false;
    }

    error = null;
    return true;
  }

  /// <summary>First occurrence at or after <paramref name="now"/>. A one-shot returns its 'at' even when past (immediately due — expiry depends on this). Null means the recurrence is exhausted (or the spec resolves to nothing).</summary>
  public DateTimeOffset? NextOnOrAfter(DateTimeOffset now)
  {
    if (At is { } at)
    {
      return at;
    }

    if (Start is { } start && Rrule is { } rrule)
    {
      var anchor = start > now ? start : now;
      return RecurrenceCalculator.NextOccurrenceOnOrAfter(start, rrule, anchor, Tz);
    }

    return null;
  }

  public DateTimeOffset? NextAfter(DateTimeOffset firedOccurrence)
  {
    return At is not null
      ? null
      : Start is { } start && Rrule is { } rrule
        ? RecurrenceCalculator.NextOccurrenceAfter(start, rrule, firedOccurrence, Tz)
        : null;
  }

  public string ToJson()
  {
    return _json;
  }

  private static DateTimeOffset? Time(JsonElement root, string name)
  {
    return root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
      ? DateTimeOffset.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
      : null;
  }

  private static string? Str(JsonElement root, string name)
  {
    return root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
  }

  // A non-object "webhook" value yields null and falls to the TryValidate grammar error.
  // Wrong-typed AND wrong-named members throw (→ Parse returns null) rather than being
  // skipped: a silently dropped or misspelled activeUntil/rateLimit would fail OPEN — a
  // never-expiring or under-limited capability URL — so the whole spec is rejected instead.
  private static WebhookSpec? ParseWebhook(JsonElement root)
  {
    if (!root.TryGetProperty("webhook", out var v) || v.ValueKind != JsonValueKind.Object)
    {
      return null;
    }

    foreach (var member in v.EnumerateObject())
    {
      if (member.Name is not ("activeAfter" or "activeUntil" or "rateLimit"))
      {
        throw new FormatException($"webhook has unknown member '{member.Name}' (valid: activeAfter, activeUntil, rateLimit)");
      }
    }

    return new WebhookSpec(StrictTime(v, "activeAfter"), StrictTime(v, "activeUntil"), StrictInt(v, "rateLimit"));
  }

  private static DateTimeOffset? StrictTime(JsonElement e, string name)
  {
    return !e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null
      ? null
      : v.ValueKind == JsonValueKind.String
        ? DateTimeOffset.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
        : throw new FormatException($"webhook '{name}' must be an ISO 8601 string");
  }

  private static int? StrictInt(JsonElement e, string name)
  {
    return !e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null
      ? null
      : v.ValueKind == JsonValueKind.Number
        ? v.GetInt32()
        : throw new FormatException($"webhook '{name}' must be an integer");
  }
}

/// <summary>The call-anchor spec: an optional validity window (checked at request time) and an optional per-webhook rate limit override.</summary>
public sealed record WebhookSpec(DateTimeOffset? ActiveAfter, DateTimeOffset? ActiveUntil, int? RateLimit);
