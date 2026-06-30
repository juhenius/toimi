using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace toimi.tools.tietue.Scheduling;

public static class RecurrenceCalculator
{
  // Forward search window; rules sparser than this won't schedule their next fire (documented limitation).
  private static readonly TimeSpan Window = TimeSpan.FromDays(366 * 2);

  public static DateTimeOffset? NextOccurrenceAfter(DateTimeOffset start, string rrule, DateTimeOffset after)
  {
    return FirstOccurrence(start, rrule, after, inclusive: false);
  }

  public static DateTimeOffset? NextOccurrenceOnOrAfter(DateTimeOffset start, string rrule, DateTimeOffset after)
  {
    return FirstOccurrence(start, rrule, after, inclusive: true);
  }

  private static DateTimeOffset? FirstOccurrence(DateTimeOffset start, string rrule, DateTimeOffset after, bool inclusive)
  {
    var calendar = new Calendar();
    calendar.Events.Add(new CalendarEvent
    {
      Start = new CalDateTime(start.UtcDateTime),
      End = new CalDateTime(start.AddHours(1).UtcDateTime),
      RecurrenceRules = [new RecurrencePattern(rrule)],
    });

    // Anchor the window at the later of `after` and DTSTART so a far-future start is still in range.
    var windowBase = after < start ? start : after;
    var from = windowBase.AddSeconds(-1).UtcDateTime;
    var to = windowBase.Add(Window).UtcDateTime;

    return calendar.GetOccurrences(new CalDateTime(from), new CalDateTime(to))
      .Select(o => o.Period.StartTime.AsDateTimeOffset)
      .Where(o => inclusive ? o >= after : o > after)
      .OrderBy(o => o)
      .Cast<DateTimeOffset?>()
      .FirstOrDefault();
  }
}
