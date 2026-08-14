using toimi.tools.tietue.Scheduling;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ScheduleTests
{
  private static readonly DateTimeOffset Now = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

  private static Schedule Parsed(string json)
  {
    var s = Schedule.Parse(json);
    Assert.NotNull(s);
    return s;
  }

  [Fact]
  public void OneShot_next_on_or_after_is_the_at_time()
  {
    var s = Parsed(/*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""");
    Assert.Equal(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), s.NextOnOrAfter(Now));
  }

  [Fact]
  public void OneShot_in_the_past_is_still_returned_immediately_due()
  {
    // Expiry depends on this: a past 'at' is due NOW, not invalid and not exhausted.
    var s = Parsed(/*lang=json,strict*/ """{"at":"2020-01-01T00:00:00Z"}""");
    Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), s.NextOnOrAfter(Now));
    Assert.True(s.TryValidate(out _));
  }

  [Fact]
  public void OneShot_next_after_fire_is_null()
  {
    var s = Parsed(/*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""");
    Assert.Null(s.NextAfter(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero)));
  }

  [Fact]
  public void Recurring_next_on_or_after_is_first_occurrence()
  {
    var s = Parsed(/*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""");
    Assert.Equal(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), s.NextOnOrAfter(Now));
  }

  [Fact]
  public void Recurring_next_after_is_following_occurrence()
  {
    var s = Parsed(/*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""");
    Assert.Equal(
      new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero),
      s.NextAfter(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero)));
  }

  [Fact]
  public void Zoned_subdaily_recurring_returns_on_grid_now()
  {
    // Regression port from SchedulesTests: real user job {start 06:30Z, MINUTELY;INTERVAL=30,
    // Europe/Helsinki} OOMed InitialNextFireAt (Ical.Net 5.2.3 DST fall-back loop + OrderBy).
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var s = Parsed(/*lang=json,strict*/ """{"start":"2026-07-31T06:30:00Z","rrule":"FREQ=MINUTELY;INTERVAL=30","tz":"Europe/Helsinki"}""");
    var next = s.NextOnOrAfter(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));
    stopwatch.Stop();

    Assert.Equal(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), next);
    Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"took {stopwatch.ElapsedMilliseconds}ms — should be immediate");
  }

  [Fact]
  public void At_wins_over_start_and_rrule()
  {
    // Precedence port: a spec carrying both is one-shot (matches the old InitialNextFireAt/NextAfter).
    var s = Parsed(/*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z","start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""");
    Assert.Equal(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), s.NextOnOrAfter(Now));
    Assert.Null(s.NextAfter(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero)));
  }

  [Theory]
  [InlineData("{ not json")]
  [InlineData("[]")]
  [InlineData("5")]
  [InlineData("null")]
  [InlineData("\"daily\"")]
  [InlineData("true")]
  [InlineData(/*lang=json,strict*/ """{"at":"soon"}""")]
  public void Unparseable_or_non_object_yields_null(string json)
  {
    Assert.Null(Schedule.Parse(json));
  }

  [Fact]
  public void WithDefaultTz_stamps_recurring_spec_without_one()
  {
    var stamped = Parsed(/*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""")
      .WithDefaultTz("Europe/Helsinki");
    Assert.Equal("Europe/Helsinki", stamped.Tz);
    using var doc = System.Text.Json.JsonDocument.Parse(stamped.ToJson());
    Assert.Equal("Europe/Helsinki", doc.RootElement.GetProperty("tz").GetString());
    Assert.Equal("FREQ=DAILY", doc.RootElement.GetProperty("rrule").GetString());
  }

  [Fact]
  public void WithDefaultTz_leaves_one_shot_unchanged()
  {
    const string json = /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""";
    var s = Parsed(json);
    Assert.Same(s, s.WithDefaultTz("Europe/Helsinki"));
    Assert.Equal(json, s.ToJson());
  }

  [Fact]
  public void WithDefaultTz_leaves_existing_tz_unchanged()
  {
    const string json = /*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY","tz":"America/New_York"}""";
    var s = Parsed(json);
    Assert.Same(s, s.WithDefaultTz("Europe/Helsinki"));
  }

  [Fact]
  public void ToJson_round_trips_the_source_including_unknown_keys()
  {
    // Storage compatibility: what the caller wrote is what gets persisted.
    const string json = /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z","note":"keep me"}""";
    Assert.Equal(json, Parsed(json).ToJson());
  }

  [Fact]
  public void OneShotAt_factory_round_trips_through_parse()
  {
    var at = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
    var s = Schedule.OneShotAt(at);
    Assert.Equal(at, s.At);
    var reparsed = Schedule.Parse(s.ToJson());
    Assert.Equal(at, reparsed!.At);
  }

  [Fact]
  public void Recurring_factory_builds_start_rrule_and_optional_tz()
  {
    var start = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
    var s = Schedule.Recurring(start, "FREQ=DAILY", "Europe/Helsinki");
    Assert.Equal(start, s.Start);
    Assert.Equal("FREQ=DAILY", s.Rrule);
    Assert.Equal("Europe/Helsinki", s.Tz);
    var bare = Schedule.Recurring(start, "FREQ=DAILY", null);
    Assert.Null(bare.Tz);
    Assert.DoesNotContain("tz", bare.ToJson());
  }

  [Fact]
  public void Validate_rejects_spec_with_neither_at_nor_start_rrule()
  {
    Assert.False(Parsed("{}").TryValidate(out var error));
    Assert.Contains("at", error);
    Assert.False(Parsed(/*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z"}""").TryValidate(out _));
  }

  [Fact]
  public void Validate_rejects_subdaily_by_part_rule_in_dst_timezone()
  {
    var s = Parsed(/*lang=json,strict*/ """{"start":"2026-01-01T00:00:00Z","rrule":"FREQ=MINUTELY;INTERVAL=30;BYHOUR=9","tz":"Europe/Helsinki"}""");
    Assert.False(s.TryValidate(out var error));
    Assert.Contains("not supported in DST timezones", error);
    Assert.Contains("tz:\"UTC\"", error);
  }

  [Fact]
  public void Validate_catches_subdaily_by_part_rule_after_default_tz_stamping()
  {
    // The order writers must use: stamp first, then validate — a tz-less rule is only
    // dangerous once the user's DST zone lands on it.
    var s = Parsed(/*lang=json,strict*/ """{"start":"2026-01-01T00:00:00Z","rrule":"FREQ=MINUTELY;INTERVAL=30;BYHOUR=9"}""");
    Assert.True(s.TryValidate(out _));
    Assert.False(s.WithDefaultTz("Europe/Helsinki").TryValidate(out _));
  }

  [Fact]
  public void Validate_accepts_subdaily_plain_interval_and_utc_escape_hatch()
  {
    Assert.True(Parsed(/*lang=json,strict*/ """{"start":"2026-01-01T00:00:00Z","rrule":"FREQ=MINUTELY;INTERVAL=30","tz":"Europe/Helsinki"}""")
      .TryValidate(out _));
    Assert.True(Parsed(/*lang=json,strict*/ """{"start":"2026-01-01T00:00:00Z","rrule":"FREQ=MINUTELY;INTERVAL=30;BYHOUR=9","tz":"UTC"}""")
      .TryValidate(out _));
  }

  [Fact]
  public void Validate_accepts_valid_one_shot_and_recurring()
  {
    Assert.True(Parsed(/*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""").TryValidate(out var e1));
    Assert.Null(e1);
    Assert.True(Parsed(/*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""").TryValidate(out _));
  }

  [Fact]
  public void Exhausted_count_rrule_is_valid_but_resolves_to_null()
  {
    // The invalid-vs-exhausted distinction: TryValidate passes, NextOnOrAfter says "spent".
    var s = Parsed(/*lang=json,strict*/ """{"start":"2020-01-01T00:00:00Z","rrule":"FREQ=YEARLY;COUNT=1"}""");
    Assert.True(s.TryValidate(out _));
    Assert.Null(s.NextOnOrAfter(Now));
  }

  [Fact]
  public void Validate_rejects_syntactically_invalid_rrule()
  {
    // Ical.Net's RecurrencePattern ctor throws ArgumentOutOfRangeException (an ArgumentException
    // subtype) for a garbage rrule string — TryValidate's catch (ArgumentException or
    // FormatException) already covers this.
    var s = Parsed(/*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"not a valid rrule"}""");
    Assert.False(s.TryValidate(out var error));
    Assert.Contains("Invalid rrule 'not a valid rrule'", error);
  }

  [Fact]
  public void Validate_rejects_syntactically_invalid_rrule_with_tz()
  {
    var s = Parsed(/*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"garbage","tz":"Europe/Helsinki"}""");
    Assert.False(s.TryValidate(out var error));
    Assert.Contains("Invalid rrule 'garbage'", error);
  }

  [Fact]
  public void Webhook_parses_spec_fields()
  {
    var s = Parsed(/*lang=json,strict*/ """{"webhook":{"activeAfter":"2026-06-01T09:00:00Z","activeUntil":"2026-06-02T09:00:00Z","rateLimit":3}}""");
    Assert.True(s.IsWebhook);
    Assert.Equal(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), s.Webhook!.ActiveAfter);
    Assert.Equal(new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero), s.Webhook.ActiveUntil);
    Assert.Equal(3, s.Webhook.RateLimit);
  }

  [Fact]
  public void Webhook_empty_spec_is_valid_and_never_time_resolves()
  {
    var s = Parsed(/*lang=json,strict*/ """{"webhook":{}}""");
    Assert.True(s.IsWebhook);
    Assert.True(s.TryValidate(out var error));
    Assert.Null(error);
    Assert.Null(s.NextOnOrAfter(Now));
    Assert.Null(s.NextAfter(Now));
    Assert.False(s.IsRecurring);
  }

  [Fact]
  public void Webhook_with_unknown_member_is_rejected()
  {
    // A misspelled activeUntil/rateLimit must not fail open into a never-expiring
    // or default-limited capability URL — the whole spec is rejected instead.
    Assert.Null(Schedule.Parse(/*lang=json,strict*/ """{"webhook":{"rateLimit":6,"note":"keep me"}}"""));
    Assert.Null(Schedule.Parse(/*lang=json,strict*/ """{"webhook":{"activeuntil":"2026-08-20T00:00:00Z"}}"""));
    Assert.Null(Schedule.Parse(/*lang=json,strict*/ """{"webhook":{"ratelimit":2}}"""));
  }

  [Fact]
  public void Webhook_with_time_anchor_fields_is_rejected()
  {
    Assert.False(Parsed(/*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z","webhook":{}}""").TryValidate(out var error));
    Assert.Contains("exactly one anchor", error);
    Assert.False(Parsed(/*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY","webhook":{}}""").TryValidate(out _));
  }

  [Fact]
  public void Webhook_with_inverted_window_is_rejected()
  {
    var s = Parsed(/*lang=json,strict*/ """{"webhook":{"activeAfter":"2026-06-02T09:00:00Z","activeUntil":"2026-06-01T09:00:00Z"}}""");
    Assert.False(s.TryValidate(out var error));
    Assert.Contains("activeAfter", error);
  }

  [Fact]
  public void Webhook_with_non_positive_rate_limit_is_rejected()
  {
    Assert.False(Parsed(/*lang=json,strict*/ """{"webhook":{"rateLimit":0}}""").TryValidate(out var error));
    Assert.Contains("rateLimit", error);
    Assert.False(Parsed(/*lang=json,strict*/ """{"webhook":{"rateLimit":-1}}""").TryValidate(out _));
  }

  [Fact]
  public void Webhook_non_object_value_fails_the_grammar()
  {
    var s = Parsed(/*lang=json,strict*/ """{"webhook":true}""");
    Assert.False(s.IsWebhook);
    Assert.False(s.TryValidate(out var error));
    Assert.Contains("webhook", error);
  }

  [Fact]
  public void Webhook_with_unparseable_date_yields_null()
  {
    Assert.Null(Schedule.Parse(/*lang=json,strict*/ """{"webhook":{"activeAfter":"soon"}}"""));
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"webhook":{"activeUntil":1767225600}}""")]
  [InlineData(/*lang=json,strict*/ """{"webhook":{"activeAfter":true}}""")]
  [InlineData(/*lang=json,strict*/ """{"webhook":{"rateLimit":"2"}}""")]
  [InlineData(/*lang=json,strict*/ """{"webhook":{"rateLimit":2.5}}""")]
  public void Webhook_with_wrong_typed_field_yields_null_instead_of_failing_open(string json)
  {
    // A silently dropped activeUntil is a never-expiring URL; a dropped rateLimit is an
    // unintended default. Wrong types reject the whole spec.
    Assert.Null(Schedule.Parse(json));
  }

  [Fact]
  public void Webhook_null_valued_fields_are_treated_as_absent()
  {
    var s = Parsed(/*lang=json,strict*/ """{"webhook":{"activeAfter":null,"rateLimit":null}}""");
    Assert.Equal(new WebhookSpec(null, null, null), s.Webhook);
    Assert.True(s.TryValidate(out _));
  }

  [Fact]
  public void WithDefaultTz_leaves_webhook_unchanged()
  {
    var s = Parsed(/*lang=json,strict*/ """{"webhook":{}}""");
    Assert.Same(s, s.WithDefaultTz("Europe/Helsinki"));
  }

  [Fact]
  public void ForWebhook_factory_round_trips_through_parse()
  {
    var after = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
    var until = new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero);
    var s = Schedule.ForWebhook(after, until, 3);
    Assert.True(s.IsWebhook);

    var reparsed = Schedule.Parse(s.ToJson());
    Assert.Equal(new WebhookSpec(after, until, 3), reparsed!.Webhook);

    var bare = Schedule.Parse(Schedule.ForWebhook().ToJson());
    Assert.Equal(new WebhookSpec(null, null, null), bare!.Webhook);
  }
}
