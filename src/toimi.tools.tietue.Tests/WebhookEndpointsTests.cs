using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Webhooks;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class WebhookEndpointsTests
{
  private static readonly DateTimeOffset Now = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

  private static HttpRequest Request(string? query = null, string? body = null, long? contentLength = null)
  {
    var context = new DefaultHttpContext();
    if (query is not null)
    {
      context.Request.QueryString = new QueryString(query);
    }

    var bytes = body is null ? [] : Encoding.UTF8.GetBytes(body);
    context.Request.Body = new MemoryStream(bytes);
    context.Request.ContentLength = contentLength ?? (body is null ? null : bytes.Length);
    return context.Request;
  }

  private static async Task<(Data.TietueDbContext db, Data.Trigger trigger)> SetupAsync(string schedule = /*lang=json,strict*/ """{"webhook":{}}""")
  {
    var db = TestDb.New();
    var trigger = await new TriggerRepository(db, TestConfig.Default).CreateAsync(
      Guid.NewGuid(), schedule, "notify", /*lang=json,strict*/ """{"titleTemplate":"ring"}""", Now);
    return (db, trigger);
  }

  private static Task<IResult> CallAsync(
    Data.TietueDbContext db, Guid triggerId, string secret, HttpRequest? request = null,
    WebhookOptions? options = null, WebhookRateLimiter? limiter = null, WebhookDispatchChannel? queue = null,
    DateTimeOffset? now = null)
  {
    return WebhookEndpoints.HandleAsync(
      triggerId, secret, request ?? Request(), db, options ?? new WebhookOptions(),
      limiter ?? new WebhookRateLimiter(), queue ?? new WebhookDispatchChannel(), now ?? Now, default);
  }

  [Fact]
  public async Task Valid_call_returns_202_with_the_enqueued_occurrence()
  {
    var (db, trigger) = await SetupAsync();
    using var _ = db;
    var queue = new WebhookDispatchChannel();

    var result = await CallAsync(db, trigger.Id, trigger.Secret!, queue: queue);

    var accepted = Assert.IsType<Accepted<WebhookEndpoints.WebhookAccepted>>(result);
    Assert.True(queue.Reader.TryRead(out var firing));
    Assert.Equal(trigger.Id, firing!.TriggerId);
    Assert.Equal(Now, firing.OccurrenceUtc);
    Assert.Equal(Now.ToString("o"), accepted.Value!.Occurrence);
  }

  [Fact]
  public async Task Merges_query_and_body_with_body_winning()
  {
    var (db, trigger) = await SetupAsync();
    using var _ = db;
    var queue = new WebhookDispatchChannel();
    var request = Request("?a=1&b=2", /*lang=json,strict*/ """{"b":3,"c":4}""");

    await CallAsync(db, trigger.Id, trigger.Secret!, request, queue: queue);

    Assert.True(queue.Reader.TryRead(out var firing));
    var @params = firing!.Params;
    Assert.Equal("1", @params.GetProperty("a").GetString());
    Assert.Equal(3, @params.GetProperty("b").GetInt32());
    Assert.Equal(4, @params.GetProperty("c").GetInt32());
  }

  [Fact]
  public async Task Get_with_no_body_yields_query_only_params()
  {
    var (db, trigger) = await SetupAsync();
    using var _ = db;
    var queue = new WebhookDispatchChannel();

    await CallAsync(db, trigger.Id, trigger.Secret!, Request("?door=front"), queue: queue);

    Assert.True(queue.Reader.TryRead(out var firing));
    Assert.Equal("front", firing!.Params.GetProperty("door").GetString());
  }

  [Fact]
  public async Task Bare_call_yields_empty_params()
  {
    var (db, trigger) = await SetupAsync();
    using var _ = db;
    var queue = new WebhookDispatchChannel();

    await CallAsync(db, trigger.Id, trigger.Secret!, queue: queue);

    Assert.True(queue.Reader.TryRead(out var firing));
    Assert.Equal(JsonValueKind.Object, firing!.Params.ValueKind);
    Assert.Equal(0, firing.Params.GetPropertyCount());
  }

  [Fact]
  public async Task All_pre_auth_failures_are_the_same_bare_404()
  {
    var (db, trigger) = await SetupAsync(
      /*lang=json,strict*/ """{"webhook":{"activeAfter":"2026-06-02T00:00:00Z","activeUntil":"2026-06-03T00:00:00Z"}}""");
    using var _ = db;
    var timeTrigger = await new TriggerRepository(db, TestConfig.Default).CreateAsync(
      Guid.NewGuid(), /*lang=json,strict*/ """{"at":"2030-01-01T00:00:00Z"}""", "notify",
      /*lang=json,strict*/ """{"titleTemplate":"x"}""", Now);
    var disabled = await SetupDisabledAsync(db);

    var results = new[]
    {
      await CallAsync(db, Guid.NewGuid(), "whatever"),                                     // unknown id
      await CallAsync(db, trigger.Id, "wrong-secret"),                                     // wrong secret
      await CallAsync(db, timeTrigger.Id, "anything"),                                     // time anchor, no secret
      await CallAsync(db, disabled.Id, disabled.Secret!),                                  // disabled
      await CallAsync(db, trigger.Id, trigger.Secret!, options: new WebhookOptions { Enabled = false }), // kill switch
      await CallAsync(db, trigger.Id, trigger.Secret!, now: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)),  // before activeAfter
      await CallAsync(db, trigger.Id, trigger.Secret!, now: new DateTimeOffset(2026, 6, 3, 0, 0, 0, TimeSpan.Zero)),  // at activeUntil
    };

    Assert.All(results, r => Assert.IsType<NotFound>(r));
  }

  [Fact]
  public async Task Call_inside_the_validity_window_is_accepted()
  {
    var (db, trigger) = await SetupAsync(
      /*lang=json,strict*/ """{"webhook":{"activeAfter":"2026-06-02T00:00:00Z","activeUntil":"2026-06-03T00:00:00Z"}}""");
    using var _ = db;

    var result = await CallAsync(db, trigger.Id, trigger.Secret!, now: new DateTimeOffset(2026, 6, 2, 12, 0, 0, TimeSpan.Zero));

    Assert.IsType<Accepted<WebhookEndpoints.WebhookAccepted>>(result);
  }

  [Fact]
  public async Task Global_cap_meters_probes_but_never_starves_valid_calls()
  {
    var (db, trigger) = await SetupAsync();
    using var _ = db;
    var limiter = new WebhookRateLimiter();
    var options = new WebhookOptions { GlobalRateLimitPerMinute = 2 };

    Assert.IsType<NotFound>(await CallAsync(db, Guid.NewGuid(), "probe", options: options, limiter: limiter));
    Assert.IsType<NotFound>(await CallAsync(db, Guid.NewGuid(), "probe", options: options, limiter: limiter));

    // The window is spent by failed-auth probes: further probes get 429...
    var probe = await CallAsync(db, Guid.NewGuid(), "probe", options: options, limiter: limiter);
    Assert.Equal(429, Assert.IsType<StatusCodeHttpResult>(probe).StatusCode);

    // ...but a VALID capability call never touches the global bucket and still lands.
    var valid = await CallAsync(db, trigger.Id, trigger.Secret!, options: options, limiter: limiter);
    Assert.IsType<Accepted<WebhookEndpoints.WebhookAccepted>>(valid);
  }

  [Fact]
  public async Task Seventh_call_in_a_minute_is_429_by_default()
  {
    var (db, trigger) = await SetupAsync();
    using var _ = db;
    var limiter = new WebhookRateLimiter();

    for (var i = 0; i < 6; i++)
    {
      Assert.IsType<Accepted<WebhookEndpoints.WebhookAccepted>>(await CallAsync(db, trigger.Id, trigger.Secret!, limiter: limiter));
    }

    var result = await CallAsync(db, trigger.Id, trigger.Secret!, limiter: limiter);
    Assert.Equal(429, Assert.IsType<StatusCodeHttpResult>(result).StatusCode);
  }

  [Fact]
  public async Task Anchor_rate_limit_overrides_the_default()
  {
    var (db, trigger) = await SetupAsync(/*lang=json,strict*/ """{"webhook":{"rateLimit":2}}""");
    using var _ = db;
    var limiter = new WebhookRateLimiter();

    await CallAsync(db, trigger.Id, trigger.Secret!, limiter: limiter);
    await CallAsync(db, trigger.Id, trigger.Secret!, limiter: limiter);
    var result = await CallAsync(db, trigger.Id, trigger.Secret!, limiter: limiter);

    Assert.Equal(429, Assert.IsType<StatusCodeHttpResult>(result).StatusCode);
  }

  [Fact]
  public async Task Declared_oversize_body_is_413()
  {
    var (db, trigger) = await SetupAsync();
    using var _ = db;

    var result = await CallAsync(db, trigger.Id, trigger.Secret!,
      Request(body: "{}", contentLength: 70_000), new WebhookOptions { MaxBodyBytes = 1024 });

    Assert.Equal(413, Assert.IsType<StatusCodeHttpResult>(result).StatusCode);
  }

  [Fact]
  public async Task Undeclared_oversize_stream_is_413()
  {
    // Chunked transfer has no Content-Length; the capped reader must still stop it.
    var (db, trigger) = await SetupAsync();
    using var _ = db;
    var request = Request(body: new string('x', 2048));
    request.ContentLength = null;

    var result = await CallAsync(db, trigger.Id, trigger.Secret!, request, new WebhookOptions { MaxBodyBytes = 1024 });

    Assert.Equal(413, Assert.IsType<StatusCodeHttpResult>(result).StatusCode);
  }

  [Theory]
  [InlineData("{ not json")]
  [InlineData("[1,2]")]
  [InlineData("\"text\"")]
  public async Task Non_object_body_is_400(string body)
  {
    var (db, trigger) = await SetupAsync();
    using var _ = db;

    var result = await CallAsync(db, trigger.Id, trigger.Secret!, Request(body: body));

    Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
  }

  [Fact]
  public async Task Full_queue_is_503()
  {
    var (db, trigger) = await SetupAsync();
    using var _ = db;
    var queue = new WebhookDispatchChannel();
    for (var i = 0; i < WebhookDispatchChannel.Capacity; i++)
    {
      using var doc = JsonDocument.Parse("{}");
      Assert.True(queue.TryEnqueue(new WebhookFiring(trigger.Id, Now, doc.RootElement.Clone())));
    }

    var result = await CallAsync(db, trigger.Id, trigger.Secret!,
      limiter: new WebhookRateLimiter(), queue: queue, options: new WebhookOptions { RateLimitPerMinute = 1000 });

    Assert.Equal(503, Assert.IsType<StatusCodeHttpResult>(result).StatusCode);
  }

  [Fact]
  public void Url_composes_from_public_base_url_and_is_null_without_one()
  {
    var trigger = new Data.Trigger { Id = Guid.NewGuid(), Schedule = "{}", HandlerKind = "notify", Secret = "S3CRET" };

    Assert.Equal(
      $"https://toimi.example/hooks/{trigger.Id}/S3CRET",
      WebhookEndpoints.Url(new WebhookOptions { PublicBaseUrl = "https://toimi.example/" }, trigger));
    Assert.Null(WebhookEndpoints.Url(new WebhookOptions(), trigger));
    Assert.Null(WebhookEndpoints.Url(
      new WebhookOptions { PublicBaseUrl = "https://toimi.example" },
      new Data.Trigger { Id = Guid.NewGuid(), Schedule = "{}", HandlerKind = "notify" }));
  }

  private static async Task<Data.Trigger> SetupDisabledAsync(Data.TietueDbContext db)
  {
    var repo = new TriggerRepository(db, TestConfig.Default);
    var trigger = await repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"webhook":{}}""", "notify",
      /*lang=json,strict*/ """{"titleTemplate":"x"}""", Now);
    await repo.UpdateAsync(trigger.Id, null, null, false, Now);
    return trigger;
  }
}
