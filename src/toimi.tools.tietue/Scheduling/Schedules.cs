using System.Text.Json;

namespace toimi.tools.tietue.Scheduling;

public static class Schedules
{
  public static DateTimeOffset? InitialNextFireAt(string scheduleJson, DateTimeOffset now)
  {
    var spec = Parse(scheduleJson);
    if (spec is null)
    {
      return null;
    }

    if (spec.At is { } at)
    {
      return at;
    }

    if (spec.Start is { } start && spec.Rrule is { } rrule)
    {
      var anchor = start > now ? start : now;
      return RecurrenceCalculator.NextOccurrenceOnOrAfter(start, rrule, anchor);
    }

    return null;
  }

  public static DateTimeOffset? NextAfter(string scheduleJson, DateTimeOffset firedOccurrence)
  {
    var spec = Parse(scheduleJson);
    return spec is null || spec.At is not null
      ? null
      : spec.Start is { } start && spec.Rrule is { } rrule
        ? RecurrenceCalculator.NextOccurrenceAfter(start, rrule, firedOccurrence)
        : null;
  }

  private sealed record Spec(DateTimeOffset? At, DateTimeOffset? Start, string? Rrule, string? Tz);

  private static Spec? Parse(string scheduleJson)
  {
    try
    {
      using var doc = JsonDocument.Parse(scheduleJson);
      var root = doc.RootElement;
      DateTimeOffset? at = root.TryGetProperty("at", out var a) && a.ValueKind == JsonValueKind.String
        ? DateTimeOffset.Parse(a.GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
        : null;
      DateTimeOffset? start = root.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.String
        ? DateTimeOffset.Parse(s.GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
        : null;
      var rrule = root.TryGetProperty("rrule", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
      var tz = root.TryGetProperty("tz", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
      return new Spec(at, start, rrule, tz);
    }
    catch (Exception ex) when (ex is JsonException or FormatException)
    {
      return null;
    }
  }
}
