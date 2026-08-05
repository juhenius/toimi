using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ical.Net.DataTypes;

namespace toimi.tools.tietue.Scheduling;

/// <summary>
/// The single owner of the trigger schedule grammar: one-shot {"at":"&lt;iso utc&gt;"} or recurring
/// {"start":"&lt;iso utc&gt;","rrule":"FREQ=...","tz":"&lt;iana&gt;"}. Every instance is grammatically
/// parseable by construction (Parse or the typed factories); TryValidate covers the semantic
/// rules writers enforce. ToJson returns exactly the JSON the schedule was built from (plus any
/// stamped tz), so persisted schedules stay compatible with what callers wrote.
/// </summary>
public sealed class Schedule
{
  private readonly string _json;

  private Schedule(string json, DateTimeOffset? at, DateTimeOffset? start, string? rrule, string? tz)
  {
    _json = json;
    At = at;
    Start = start;
    Rrule = rrule;
    Tz = tz;
  }

  public DateTimeOffset? At { get; }
  public DateTimeOffset? Start { get; }
  public string? Rrule { get; }
  public string? Tz { get; }

  /// <summary>A spec with 'at' is one-shot even when rrule fields are also present ('at' wins in NextOnOrAfter/NextAfter).</summary>
  public bool IsRecurring => At is null && Start is not null && Rrule is not null;

  /// <summary>Null when the JSON is not an object or a date field doesn't parse — the grammar is unmet.</summary>
  public static Schedule? Parse(string json)
  {
    try
    {
      using var doc = JsonDocument.Parse(json);
      var root = doc.RootElement;
      return root.ValueKind != JsonValueKind.Object
        ? null
        : new Schedule(json, Time(root, "at"), Time(root, "start"), Str(root, "rrule"), Str(root, "tz"));
    }
    catch (Exception ex) when (ex is JsonException or FormatException)
    {
      return null;
    }
  }

  public static Schedule OneShotAt(DateTimeOffset at)
  {
    var utc = at.ToUniversalTime();
    return new Schedule(new JsonObject { ["at"] = utc.ToString("o") }.ToJsonString(), utc, null, null, null);
  }

  public static Schedule Recurring(DateTimeOffset start, string rrule, string? tz = null)
  {
    var utc = start.ToUniversalTime();
    var node = new JsonObject { ["start"] = utc.ToString("o"), ["rrule"] = rrule };
    if (tz is not null)
    {
      node["tz"] = tz;
    }

    return new Schedule(node.ToJsonString(), null, utc, rrule, tz);
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
    return new Schedule(node.ToJsonString(), At, Start, Rrule, defaultTz);
  }

  /// <summary>
  /// The provably-invalid checks, independent of the clock: grammar, rrule syntax, and the
  /// sub-daily+BY-parts+DST-tz combination the calculator refuses. Deliberately does NOT
  /// reject elapsed one-shots or exhausted recurrences — that distinction is the caller's,
  /// via NextOnOrAfter (see TriggerRepository).
  /// </summary>
  public bool TryValidate(out string? error)
  {
    if (At is null && !IsRecurring)
    {
      error = "Schedule must be {\"at\":\"<iso utc>\"} (one-shot) or {\"start\":\"<iso utc>\",\"rrule\":\"FREQ=...\"} (recurring).";
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
}
