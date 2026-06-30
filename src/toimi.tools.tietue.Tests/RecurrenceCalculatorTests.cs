using toimi.tools.tietue.Scheduling;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class RecurrenceCalculatorTests
{
  private static readonly DateTimeOffset Start = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero); // Mon 2026-06-01 09:00Z

  [Fact]
  public void Daily_next_after_returns_next_day()
  {
    var next = RecurrenceCalculator.NextOccurrenceAfter(Start, "FREQ=DAILY", new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));
    Assert.Equal(new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero), next);
  }

  [Fact]
  public void OnOrAfter_is_inclusive_of_an_exact_occurrence()
  {
    var next = RecurrenceCalculator.NextOccurrenceOnOrAfter(Start, "FREQ=DAILY", new(2026, 6, 3, 9, 0, 0, TimeSpan.Zero));
    Assert.Equal(new DateTimeOffset(2026, 6, 3, 9, 0, 0, TimeSpan.Zero), next);
  }

  [Fact]
  public void Bounded_rule_returns_null_after_last_occurrence()
  {
    var next = RecurrenceCalculator.NextOccurrenceAfter(Start, "FREQ=DAILY;COUNT=3", new(2026, 6, 3, 9, 0, 0, TimeSpan.Zero));
    Assert.Null(next);
  }

  [Fact]
  public void Weekly_byday_skips_to_matching_weekday()
  {
    var next = RecurrenceCalculator.NextOccurrenceAfter(Start, "FREQ=WEEKLY;BYDAY=MO", new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));
    Assert.Equal(new DateTimeOffset(2026, 6, 8, 9, 0, 0, TimeSpan.Zero), next);
  }

  [Fact]
  public void OnOrAfter_with_far_future_start_returns_start()
  {
    var farStart = new DateTimeOffset(2030, 1, 1, 9, 0, 0, TimeSpan.Zero);
    var next = RecurrenceCalculator.NextOccurrenceOnOrAfter(farStart, "FREQ=DAILY", new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
    Assert.Equal(farStart, next);
  }

  [Fact]
  public void Until_bounded_returns_null_after_end()
  {
    var next = RecurrenceCalculator.NextOccurrenceAfter(Start, "FREQ=DAILY;UNTIL=20260603T090000Z", new(2026, 6, 3, 9, 0, 0, TimeSpan.Zero));
    Assert.Null(next);
  }
}
