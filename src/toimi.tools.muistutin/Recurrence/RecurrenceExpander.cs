using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace toimi.tools.muistutin.Recurrence;

public static class RecurrenceExpander
{
  private const int DisplayDateSearchRangeInYears = 200;

  public static DateTimeOffset? ComputeDisplayEndUtc(DateTimeOffset dateTimeUtc, string? recurrenceRule)
  {
    if (string.IsNullOrEmpty(recurrenceRule))
    {
      return null;
    }

    // Open-ended recurrence (no COUNT or UNTIL) has no display end
    if (!recurrenceRule.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
        !recurrenceRule.Contains("UNTIL", StringComparison.OrdinalIgnoreCase))
    {
      return null;
    }

    var calendar = new Calendar();
    calendar.Events.Add(ToCalendarEvent(dateTimeUtc, recurrenceRule));

    var rangeStart = CalDateTime.Now.AddYears(-DisplayDateSearchRangeInYears);
    var rangeEnd = CalDateTime.Now.AddYears(DisplayDateSearchRangeInYears);

    var lastOccurrence = calendar.GetOccurrences(rangeStart, rangeEnd)
        .MaxBy(o => o.Period.StartTime);

    return lastOccurrence?.Period.StartTime.AsDateTimeOffset ?? dateTimeUtc;
  }

  public static IEnumerable<DateTimeOffset> ExpandOccurrences(DateTimeOffset dateTimeUtc, string? recurrenceRule, DateTimeOffset from, DateTimeOffset to)
  {
    if (string.IsNullOrEmpty(recurrenceRule))
    {
      if (dateTimeUtc >= from && dateTimeUtc <= to)
      {
        yield return dateTimeUtc;
      }

      yield break;
    }

    var calendar = new Calendar();
    calendar.Events.Add(ToCalendarEvent(dateTimeUtc, recurrenceRule));

    foreach (var occurrence in calendar.GetOccurrences(ToIDateTime(from), ToIDateTime(to)))
    {
      yield return occurrence.Period.StartTime.AsDateTimeOffset;
    }
  }

  private static CalendarEvent ToCalendarEvent(DateTimeOffset dateTimeUtc, string recurrenceRule)
  {
    return new CalendarEvent
    {
      Start = ToIDateTime(dateTimeUtc),
      End = ToIDateTime(dateTimeUtc.AddHours(1)),
      RecurrenceRules = [new RecurrencePattern(recurrenceRule)]
    };
  }

  private static CalDateTime ToIDateTime(DateTimeOffset dt)
  {
    return new CalDateTime(dt.UtcDateTime);
  }
}
