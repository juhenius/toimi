using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace toimi.tools.tietue.Scheduling;

public static class RecurrenceCalculator
{
  // Forward search window; rules sparser than this won't schedule their next fire (documented limitation).
  private static readonly TimeSpan Window = TimeSpan.FromDays(366 * 2);

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
    var tzInfo = ResolveTz(tz);
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
      RecurrenceRule = new RecurrencePattern(rrule),
    });

    // Anchor the window at the later of `after` and DTSTART so a far-future start is still in range.
    var windowBase = after < start ? start : after;
    var from = windowBase.AddSeconds(-1).UtcDateTime;
    var to = windowBase.Add(Window).UtcDateTime;

    // Ical.Net 5: GetOccurrences takes only a start and yields an ordered, unbounded sequence;
    // TakeWhileBefore re-applies the historical window cap.
    return calendar.GetOccurrences(new CalDateTime(from))
      .TakeWhileBefore(new CalDateTime(to))
      // AsUtc carries the occurrence's instant in UTC; compare instants in UTC so the
      // inclusive/exclusive boundary is correct regardless of the zone.
      .Select(o => new DateTimeOffset(o.Period.StartTime.AsUtc))
      .Where(o => inclusive ? o >= after : o > after)
      .OrderBy(o => o)
      .Cast<DateTimeOffset?>()
      .FirstOrDefault();
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
