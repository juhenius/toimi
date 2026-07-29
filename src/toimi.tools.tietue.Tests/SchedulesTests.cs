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

  [Fact]
  public void WithDefaultTimeZone_stamps_tz_on_recurring_spec_without_one()
  {
    var stamped = Schedules.WithDefaultTimeZone(
      /*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""", "Europe/Helsinki");
    using var doc = System.Text.Json.JsonDocument.Parse(stamped);
    Assert.Equal("Europe/Helsinki", doc.RootElement.GetProperty("tz").GetString());
    Assert.Equal("FREQ=DAILY", doc.RootElement.GetProperty("rrule").GetString());
  }

  [Fact]
  public void WithDefaultTimeZone_leaves_one_shot_unchanged()
  {
    const string json = /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""";
    Assert.Equal(json, Schedules.WithDefaultTimeZone(json, "Europe/Helsinki"));
  }

  [Fact]
  public void WithDefaultTimeZone_leaves_existing_tz_unchanged()
  {
    const string json = /*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY","tz":"America/New_York"}""";
    Assert.Equal(json, Schedules.WithDefaultTimeZone(json, "Europe/Helsinki"));
  }

  [Fact]
  public void WithDefaultTimeZone_leaves_unparseable_unchanged()
  {
    const string json = "{ not json";
    Assert.Equal(json, Schedules.WithDefaultTimeZone(json, "Europe/Helsinki"));
  }

  [Theory]
  [InlineData("[]")]
  [InlineData("5")]
  [InlineData("null")]
  [InlineData("\"daily\"")]
  [InlineData("true")]
  public void Non_object_schedule_json_yields_null_not_a_crash(string scheduleJson)
  {
    // Valid JSON that is not an object must behave like any other unparseable
    // schedule: null fire time, spec passed through unchanged — not an
    // InvalidOperationException escaping the MCP tool.
    Assert.Null(Schedules.InitialNextFireAt(scheduleJson, Now));
    Assert.Null(Schedules.NextAfter(scheduleJson, Now));
    Assert.Equal(scheduleJson, Schedules.WithDefaultTimeZone(scheduleJson, "Europe/Helsinki"));
  }
}
