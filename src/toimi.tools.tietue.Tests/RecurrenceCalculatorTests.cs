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

  // ---- Sub-daily rules (arithmetic fast path; Ical.Net 5.2.3 cannot cross a DST
  // fall-back for tz-anchored sub-daily rules — see RecurrenceCalculator.FirstOccurrence).

  private static readonly DateTimeOffset SubDailyStart = new(2026, 7, 31, 6, 30, 0, TimeSpan.Zero);

  [Fact]
  public void Zoned_subdaily_continues_through_dst_fall_back_with_absolute_spacing()
  {
    // 2026-10-25 Helsinki falls back; sub-daily intervals are exact durations per
    // RFC 5545, so the 30-min grid must continue in absolute time through the transition.
    var next = RecurrenceCalculator.NextOccurrenceOnOrAfter(
      SubDailyStart, "FREQ=MINUTELY;INTERVAL=30", new DateTimeOffset(2026, 10, 25, 0, 45, 0, TimeSpan.Zero), "Europe/Helsinki");
    Assert.Equal(new DateTimeOffset(2026, 10, 25, 1, 0, 0, TimeSpan.Zero), next);
  }

  [Fact]
  public void Zoned_subdaily_next_after_grid_point_inside_fall_back_advances()
  {
    var next = RecurrenceCalculator.NextOccurrenceAfter(
      SubDailyStart, "FREQ=MINUTELY;INTERVAL=30", new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), "Europe/Helsinki");
    Assert.Equal(new DateTimeOffset(2026, 10, 25, 1, 0, 0, TimeSpan.Zero), next);
  }

  [Fact]
  public void Zoned_subdaily_continues_through_spring_forward()
  {
    // 2026-03-29 Helsinki springs forward; absolute 30-min spacing is unaffected.
    var start = new DateTimeOffset(2026, 1, 5, 6, 30, 0, TimeSpan.Zero);
    var next = RecurrenceCalculator.NextOccurrenceOnOrAfter(
      start, "FREQ=MINUTELY;INTERVAL=30", new DateTimeOffset(2026, 3, 29, 0, 45, 0, TimeSpan.Zero), "Europe/Helsinki");
    Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), next);
  }

  [Fact]
  public void Subdaily_count_bound_is_honored()
  {
    const string rule = "FREQ=MINUTELY;INTERVAL=30;COUNT=4"; // 06:30, 07:00, 07:30, 08:00

    var first = RecurrenceCalculator.NextOccurrenceOnOrAfter(SubDailyStart, rule, SubDailyStart);
    Assert.Equal(SubDailyStart, first);

    var fourth = RecurrenceCalculator.NextOccurrenceAfter(
      SubDailyStart, rule, new DateTimeOffset(2026, 7, 31, 7, 30, 0, TimeSpan.Zero));
    Assert.Equal(new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero), fourth);

    var afterLast = RecurrenceCalculator.NextOccurrenceAfter(
      SubDailyStart, rule, new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero));
    Assert.Null(afterLast);
  }

  [Fact]
  public void Subdaily_until_bound_is_honored_inclusively()
  {
    const string rule = "FREQ=MINUTELY;INTERVAL=30;UNTIL=20260731T073000Z";

    var atBoundary = RecurrenceCalculator.NextOccurrenceOnOrAfter(
      SubDailyStart, rule, new DateTimeOffset(2026, 7, 31, 7, 30, 0, TimeSpan.Zero));
    Assert.Equal(new DateTimeOffset(2026, 7, 31, 7, 30, 0, TimeSpan.Zero), atBoundary);

    var pastBoundary = RecurrenceCalculator.NextOccurrenceAfter(
      SubDailyStart, rule, new DateTimeOffset(2026, 7, 31, 7, 30, 0, TimeSpan.Zero));
    Assert.Null(pastBoundary);
  }

  [Fact]
  public void Subdaily_without_tz_lands_on_next_grid_point()
  {
    // Pre-change parity: pure-UTC sub-daily expansion already produced the 30-min grid.
    var next = RecurrenceCalculator.NextOccurrenceOnOrAfter(
      SubDailyStart, "FREQ=MINUTELY;INTERVAL=30", new DateTimeOffset(2026, 7, 31, 7, 10, 0, TimeSpan.Zero));
    Assert.Equal(new DateTimeOffset(2026, 7, 31, 7, 30, 0, TimeSpan.Zero), next);
  }

  [Fact]
  public void Zoned_hourly_interval_lands_on_grid()
  {
    var next = RecurrenceCalculator.NextOccurrenceOnOrAfter(
      SubDailyStart, "FREQ=HOURLY;INTERVAL=6", new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.Zero), "Europe/Helsinki");
    Assert.Equal(new DateTimeOffset(2026, 7, 31, 12, 30, 0, TimeSpan.Zero), next);
  }

  [Fact]
  public void Zoned_secondly_interval_lands_on_grid()
  {
    var next = RecurrenceCalculator.NextOccurrenceOnOrAfter(
      SubDailyStart, "FREQ=SECONDLY;INTERVAL=90", new DateTimeOffset(2026, 7, 31, 6, 31, 0, TimeSpan.Zero), "Europe/Helsinki");
    Assert.Equal(new DateTimeOffset(2026, 7, 31, 6, 31, 30, TimeSpan.Zero), next);
  }

  [Fact]
  public void Zoned_subdaily_with_by_part_is_refused_promptly()
  {
    // Ical.Net 5.2.3 cannot expand tz-anchored sub-daily BY-part rules across a DST
    // fall-back (it loops or hangs internally), so the calculator refuses them outright.
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var next = RecurrenceCalculator.NextOccurrenceOnOrAfter(
      SubDailyStart, "FREQ=MINUTELY;INTERVAL=30;BYHOUR=9", SubDailyStart, "Europe/Helsinki");
    stopwatch.Stop();

    Assert.Null(next);
    Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"refusal took {stopwatch.ElapsedMilliseconds}ms — should be immediate");
  }

  [Fact]
  public void Subdaily_with_by_part_in_non_dst_zone_is_still_evaluated()
  {
    // A zone without DST transitions (tz "UTC", fixed offsets) is provably safe for
    // Ical.Net's zoned expansion — only DST-observing zones are refused.
    var next = RecurrenceCalculator.NextOccurrenceOnOrAfter(
      SubDailyStart, "FREQ=MINUTELY;INTERVAL=30;BYHOUR=9", SubDailyStart, "UTC");
    Assert.Equal(new DateTimeOffset(2026, 7, 31, 9, 0, 0, TimeSpan.Zero), next);
  }

  [Theory]
  [InlineData("FREQ=MINUTELY;INTERVAL=30;BYHOUR=9", "Europe/Helsinki", true)] // sub-daily + BY* + DST zone
  [InlineData("FREQ=MINUTELY;INTERVAL=30;BYHOUR=9", "UTC", false)] // non-DST zone is safe
  [InlineData("FREQ=MINUTELY;INTERVAL=30;BYHOUR=9", null, false)] // no tz -> pure UTC is safe
  [InlineData("FREQ=MINUTELY;INTERVAL=30", "Europe/Helsinki", false)] // plain interval -> fast path
  [InlineData("FREQ=DAILY;BYHOUR=9", "Europe/Helsinki", false)] // daily and coarser are fine
  [InlineData("not an rrule", "Europe/Helsinki", false)] // malformed -> normal validation reports it
  public void IsUnsupportedSubDaily_flags_only_zoned_dst_by_part_rules(string rrule, string? tz, bool expected)
  {
    Assert.Equal(expected, RecurrenceCalculator.IsUnsupportedSubDaily(rrule, tz));
  }

  [Fact]
  public void Subdaily_with_by_part_without_tz_is_still_evaluated()
  {
    // No tz -> pure-UTC Ical.Net expansion is safe. MINUTELY;INTERVAL=60 from 06:30Z
    // hits hh:30 each hour; BYHOUR=9 filters to 09:30Z.
    var next = RecurrenceCalculator.NextOccurrenceOnOrAfter(
      SubDailyStart, "FREQ=MINUTELY;INTERVAL=60;BYHOUR=9", new DateTimeOffset(2026, 7, 31, 7, 0, 0, TimeSpan.Zero));
    Assert.Equal(new DateTimeOffset(2026, 7, 31, 9, 30, 0, TimeSpan.Zero), next);
  }
}
