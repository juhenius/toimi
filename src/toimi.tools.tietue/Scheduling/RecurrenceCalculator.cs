using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace toimi.tools.tietue.Scheduling;

public static class RecurrenceCalculator
{
  // Forward search window; rules sparser than this won't schedule their next fire (documented limitation).
  private static readonly TimeSpan Window = TimeSpan.FromDays(366 * 2);

  // Backstop against a runaway Ical.Net enumeration (see the monotonicity guard below).
  private const int MaxScannedOccurrences = 2_000_000;

  public static DateTimeOffset? NextOccurrenceAfter(DateTimeOffset start, string rrule, DateTimeOffset after, string? tz = null)
  {
    return FirstOccurrence(start, rrule, after, inclusive: false, tz);
  }

  public static DateTimeOffset? NextOccurrenceOnOrAfter(DateTimeOffset start, string rrule, DateTimeOffset after, string? tz = null)
  {
    return FirstOccurrence(start, rrule, after, inclusive: true, tz);
  }

  private static DateTimeOffset? FirstOccurrence(DateTimeOffset start, string rrule, DateTimeOffset after, bool inclusive, string? tz = null)
  {
    var pattern = new RecurrencePattern(rrule);
    var tzInfo = ResolveTz(tz);

    if (IsSubDaily(pattern.Frequency))
    {
      if (!HasByRules(pattern))
      {
        // Sub-daily intervals are exact durations per RFC 5545: occurrences are
        // start + k*step in absolute time, so DST is irrelevant by construction and
        // any tz is ignored. Computed arithmetically — Ical.Net 5.2.3's evaluator
        // cannot cross a DST fall-back for tz-anchored sub-daily rules (it loops on
        // the repeated hour forever).
        return ArithmeticNext(pattern, start, after, inclusive);
      }

      if (IsUnsupportedSubDaily(pattern, tzInfo))
      {
        // Ical.Net 5.2.3 limitation: a sub-daily rule with BY-part filters anchored to
        // a DST-observing zone either loops on a fall-back's repeated hour or hangs
        // internally when anchored at/past the transition — there is no safe
        // enumeration and no timeout above us. Refuse deterministically.
        return null;
      }

      // Sub-daily + BY-parts without a DST-observing tz (none at all, UTC, or a fixed
      // offset) has no transitions to trip over and expands safely below.
    }

    // With a resolved zone, anchor DTSTART to that zone's wall-clock so Ical.Net expands the
    // rule in local time (DST-stable); without one, keep the historical pure-UTC expansion.
    var startCal = tzInfo is null
      ? new CalDateTime(start.UtcDateTime)
      : new CalDateTime(TimeZoneInfo.ConvertTime(start, tzInfo).DateTime, tz);

    var calendar = new Calendar();
    calendar.Events.Add(new CalendarEvent
    {
      Start = startCal,
      Duration = Duration.FromHours(1),
      RecurrenceRule = pattern,
    });

    // Anchor the window at the later of `after` and DTSTART so a far-future start is still in range.
    var windowBase = after < start ? start : after;
    var from = windowBase.AddSeconds(-1).UtcDateTime;
    var to = windowBase.Add(Window).UtcDateTime;

    // Ical.Net 5: GetOccurrences takes only a start and yields an ordered, unbounded sequence;
    // TakeWhileBefore re-applies the historical window cap. The sequence is consumed lazily
    // (no OrderBy — it forced full materialization of the window and amplified evaluator
    // bugs into OOM) with a monotonicity guard and a hard scan cap as backstops.
    var occurrences = calendar.GetOccurrences(new CalDateTime(from)).TakeWhileBefore(new CalDateTime(to));
    return MonotonicUtcInstants(occurrences)
      .Take(MaxScannedOccurrences)
      .Where(o => inclusive ? o >= after : o > after)
      .Cast<DateTimeOffset?>()
      .FirstOrDefault();
  }

  private static DateTimeOffset? ArithmeticNext(RecurrencePattern pattern, DateTimeOffset start, DateTimeOffset after, bool inclusive)
  {
    var interval = Math.Max(1, pattern.Interval);
    var step = pattern.Frequency switch
    {
      FrequencyType.Secondly => TimeSpan.FromSeconds(interval),
      FrequencyType.Minutely => TimeSpan.FromMinutes(interval),
      FrequencyType.Hourly => TimeSpan.FromHours(interval),
      // Daily and coarser never reach ArithmeticNext (see FirstOccurrence).
      FrequencyType.Daily or FrequencyType.Weekly or FrequencyType.Monthly or FrequencyType.Yearly or _ =>
        throw new ArgumentOutOfRangeException(nameof(pattern), pattern.Frequency, "ArithmeticNext handles sub-daily frequencies only"),
    };

    // Smallest k with start + k*step >= after (clamped to 0 when after <= start).
    var k = 0L;
    var diffTicks = (after - start).Ticks;
    if (diffTicks > 0)
    {
      k = diffTicks / step.Ticks;
      if (diffTicks % step.Ticks != 0)
      {
        k++;
      }
    }

    var occurrence = start + new TimeSpan(k * step.Ticks);
    if (!inclusive && occurrence == after)
    {
      k++;
      occurrence = start + new TimeSpan(k * step.Ticks);
    }

    if (pattern.Count is { } count && k >= count)
    {
      return null; // occurrence k is 0-based; COUNT=n allows k = 0..n-1
    }

    if (pattern.Until is { } until && occurrence > new DateTimeOffset(until.AsUtc))
    {
      return null; // UNTIL is inclusive
    }

    // Preserve the documented forward-window limitation of the Ical.Net path.
    var windowBase = after < start ? start : after;
    return occurrence >= windowBase.Add(Window) ? null : occurrence;
  }

  // The Ical.Net sequence is ordered by contract; a decreasing UTC instant is the signature
  // of the 5.2.3 DST fall-back loop (oscillating on the repeated hour) — stop instead of spinning.
  private static IEnumerable<DateTimeOffset> MonotonicUtcInstants(IEnumerable<Occurrence> occurrences)
  {
    DateTimeOffset? previous = null;
    foreach (var occurrence in occurrences)
    {
      // AsUtc carries the occurrence's instant in UTC; comparing instants in UTC keeps the
      // inclusive/exclusive boundary correct regardless of the zone.
      var instant = new DateTimeOffset(occurrence.Period.StartTime.AsUtc);
      if (previous is { } p && instant < p)
      {
        yield break;
      }

      previous = instant;
      yield return instant;
    }
  }

  /// <summary>
  /// True when <paramref name="rrule"/> is a sub-daily rule with BY-part filters anchored to a
  /// DST-observing zone — the combination Ical.Net 5.2.3 cannot evaluate (see FirstOccurrence).
  /// Malformed rules return false so normal schedule validation reports them instead.
  /// </summary>
  public static bool IsUnsupportedSubDaily(string rrule, string? tz)
  {
    try
    {
      return IsUnsupportedSubDaily(new RecurrencePattern(rrule), ResolveTz(tz));
    }
    catch (ArgumentException)
    {
      return false;
    }
  }

  private static bool IsUnsupportedSubDaily(RecurrencePattern pattern, TimeZoneInfo? tzInfo)
  {
    return IsSubDaily(pattern.Frequency) && HasByRules(pattern) && tzInfo is { SupportsDaylightSavingTime: true };
  }

  private static bool IsSubDaily(FrequencyType frequency)
  {
    return frequency is FrequencyType.Secondly or FrequencyType.Minutely or FrequencyType.Hourly;
  }

  private static bool HasByRules(RecurrencePattern pattern)
  {
    return pattern.BySecond.Count > 0 || pattern.ByMinute.Count > 0 || pattern.ByHour.Count > 0
      || pattern.ByDay.Count > 0 || pattern.ByMonthDay.Count > 0 || pattern.ByYearDay.Count > 0
      || pattern.ByWeekNo.Count > 0 || pattern.ByMonth.Count > 0 || pattern.BySetPosition.Count > 0;
  }

  private static TimeZoneInfo? ResolveTz(string? tz)
  {
    if (string.IsNullOrWhiteSpace(tz))
    {
      return null;
    }

    try
    {
      return TimeZoneInfo.FindSystemTimeZoneById(tz);
    }
    catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
    {
      return null; // unknown tz -> fall back to UTC expansion rather than throwing
    }
  }
}
