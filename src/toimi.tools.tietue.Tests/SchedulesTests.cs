using toimi.tools.tietue.Scheduling;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class SchedulesTests
{
  private static readonly DateTimeOffset Now = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

  [Fact]
  public void OneShot_initial_is_the_at_time()
  {
    var next = Schedules.InitialNextFireAt(/*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", Now);
    Assert.Equal(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), next);
  }

  [Fact]
  public void OneShot_next_after_fire_is_null()
  {
    Assert.Null(Schedules.NextAfter(/*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero)));
  }

  [Fact]
  public void Recurring_initial_is_first_occurrence_on_or_after_now()
  {
    var next = Schedules.InitialNextFireAt(/*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""", Now);
    Assert.Equal(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), next);
  }

  [Fact]
  public void Recurring_next_after_is_following_occurrence()
  {
    var next = Schedules.NextAfter(/*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""", new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));
    Assert.Equal(new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero), next);
  }

  [Fact]
  public void Malformed_schedule_yields_null()
  {
    Assert.Null(Schedules.InitialNextFireAt("{ not json", Now));
  }
}
