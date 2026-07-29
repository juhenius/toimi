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

  [Fact]
  public void Daily_rule_in_a_timezone_keeps_wall_clock_across_dst()
  {
    // 2026-03-29 Helsinki springs forward (EET+2 -> EEST+3).
    var start = new DateTimeOffset(2026, 3, 27, 9, 0, 0, TimeSpan.FromHours(2)); // 09:00 local, 07:00Z
    var beforeDst = RecurrenceCalculator.NextOccurrenceAfter(
      start, "FREQ=DAILY", new DateTimeOffset(2026, 3, 27, 12, 0, 0, TimeSpan.Zero), "Europe/Helsinki");
    var afterDst = RecurrenceCalculator.NextOccurrenceAfter(
      start, "FREQ=DAILY", new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.Zero), "Europe/Helsinki");

    // Wall-clock stays 09:00 Helsinki; the UTC instant shifts by the DST hour.
    Assert.Equal(new DateTimeOffset(2026, 3, 28, 7, 0, 0, TimeSpan.Zero), beforeDst!.Value.ToUniversalTime());
    Assert.Equal(new DateTimeOffset(2026, 3, 30, 6, 0, 0, TimeSpan.Zero), afterDst!.Value.ToUniversalTime());
  }

  [Fact]
  public void Nonexistent_wall_clock_in_spring_forward_gap_does_not_throw_or_vanish()
  {
    // 2026-03-29 Helsinki jumps 03:00 -> 04:00; a daily 03:30 rule has no valid
    // wall-clock that day. Whatever Ical.Net does (skip/shift), the schedule must
    // survive: the occurrence after the gap day must land back on 03:30 local.
    var start = new DateTimeOffset(2026, 3, 27, 3, 30, 0, TimeSpan.FromHours(2));

    var gapDay = RecurrenceCalculator.NextOccurrenceAfter(
      start, "FREQ=DAILY", new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero), "Europe/Helsinki");
    Assert.NotNull(gapDay);

    var afterGap = RecurrenceCalculator.NextOccurrenceAfter(
      start, "FREQ=DAILY", gapDay!.Value, "Europe/Helsinki");
    // 03:30 EEST (UTC+3) on 2026-03-30 == 00:30Z.
    Assert.Equal(new DateTimeOffset(2026, 3, 30, 0, 30, 0, TimeSpan.Zero), afterGap!.Value.ToUniversalTime());
  }

  [Fact]
  public void Ambiguous_wall_clock_in_fall_back_hour_fires_exactly_once()
  {
    // 2026-10-25 Helsinki repeats 03:00-04:00; a daily 03:30 rule has two candidate
    // instants that day. Consecutive occurrences must stay ~a day apart — a
    // double-fire inside the repeated hour would send duplicate notifications.
    var start = new DateTimeOffset(2026, 10, 23, 3, 30, 0, TimeSpan.FromHours(3));

    var first = RecurrenceCalculator.NextOccurrenceAfter(
      start, "FREQ=DAILY", new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero), "Europe/Helsinki");
    Assert.NotNull(first);
    var second = RecurrenceCalculator.NextOccurrenceAfter(start, "FREQ=DAILY", first.Value, "Europe/Helsinki");
    Assert.NotNull(second);

    Assert.True(second.Value - first.Value >= TimeSpan.FromHours(20),
      $"occurrences {first:o} and {second:o} are suspiciously close — double fire in the repeated hour");
  }

  [Fact]
  public void Count_bounded_rule_across_dst_yields_exactly_count_occurrences_at_stable_wall_clock()
  {
    // Daily 09:00 Helsinki, 5 occurrences spanning the 2026-03-29 spring-forward.
    var start = new DateTimeOffset(2026, 3, 27, 9, 0, 0, TimeSpan.FromHours(2));
    var occurrences = new List<DateTimeOffset>();
    var current = RecurrenceCalculator.NextOccurrenceOnOrAfter(
      start, "FREQ=DAILY;COUNT=5", start, "Europe/Helsinki");
    while (current is not null)
    {
      occurrences.Add(current.Value);
      current = RecurrenceCalculator.NextOccurrenceAfter(start, "FREQ=DAILY;COUNT=5", current.Value, "Europe/Helsinki");
    }

    Assert.Equal(5, occurrences.Count);
    // 09:00 EET (UTC+2) -> 07:00Z before the transition; 09:00 EEST (UTC+3) -> 06:00Z after.
    Assert.Equal(new DateTimeOffset(2026, 3, 27, 7, 0, 0, TimeSpan.Zero), occurrences[0].ToUniversalTime());
    Assert.Equal(new DateTimeOffset(2026, 3, 28, 7, 0, 0, TimeSpan.Zero), occurrences[1].ToUniversalTime());
    Assert.Equal(new DateTimeOffset(2026, 3, 29, 6, 0, 0, TimeSpan.Zero), occurrences[2].ToUniversalTime());
    Assert.Equal(new DateTimeOffset(2026, 3, 30, 6, 0, 0, TimeSpan.Zero), occurrences[3].ToUniversalTime());
    Assert.Equal(new DateTimeOffset(2026, 3, 31, 6, 0, 0, TimeSpan.Zero), occurrences[4].ToUniversalTime());
  }

  [Fact]
  public void Rules_sparser_than_the_two_year_window_return_null()
  {
    // Documented limitation (RecurrenceCalculator.Window): the next occurrence of
    // FREQ=YEARLY;INTERVAL=3 is beyond the search window, so scheduling returns
    // null — and SchedulerTick then DISABLES the trigger. Pinned so a future
    // window change is a conscious decision.
    var next = RecurrenceCalculator.NextOccurrenceAfter(Start, "FREQ=YEARLY;INTERVAL=3", Start);
    Assert.Null(next);
  }

  [Fact]
  public void Unknown_timezone_falls_back_to_utc_expansion_without_throwing()
  {
    var withBogusTz = RecurrenceCalculator.NextOccurrenceAfter(Start, "FREQ=DAILY", Start, "Mars/Olympus");
    var pureUtc = RecurrenceCalculator.NextOccurrenceAfter(Start, "FREQ=DAILY", Start);
    Assert.Equal(pureUtc, withBogusTz);
  }
}
