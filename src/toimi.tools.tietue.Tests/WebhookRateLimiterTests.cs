using Microsoft.Extensions.Time.Testing;
using toimi.tools.tietue.Webhooks;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class WebhookRateLimiterTests
{
  [Fact]
  public void Allows_up_to_the_limit_then_rejects()
  {
    var limiter = new WebhookRateLimiter(new FakeTimeProvider());
    var id = Guid.NewGuid();

    for (var i = 0; i < 6; i++)
    {
      Assert.True(limiter.TryAcquire(id, 6));
    }

    Assert.False(limiter.TryAcquire(id, 6));
  }

  [Fact]
  public void Window_resets_at_the_minute_boundary()
  {
    var time = new FakeTimeProvider();
    var limiter = new WebhookRateLimiter(time);
    var id = Guid.NewGuid();

    Assert.True(limiter.TryAcquire(id, 1));
    Assert.False(limiter.TryAcquire(id, 1));

    time.Advance(TimeSpan.FromMinutes(1));
    Assert.True(limiter.TryAcquire(id, 1));
  }

  [Fact]
  public void Triggers_are_limited_independently()
  {
    var limiter = new WebhookRateLimiter(new FakeTimeProvider());

    Assert.True(limiter.TryAcquire(Guid.NewGuid(), 1));
    Assert.True(limiter.TryAcquire(Guid.NewGuid(), 1));
  }

  [Fact]
  public void Rejection_does_not_extend_the_window()
  {
    // Fixed window, not sliding: hammering while limited must not delay the reset.
    var time = new FakeTimeProvider();
    var limiter = new WebhookRateLimiter(time);
    var id = Guid.NewGuid();

    Assert.True(limiter.TryAcquire(id, 1));
    time.Advance(TimeSpan.FromSeconds(30));
    Assert.False(limiter.TryAcquire(id, 1));
    time.Advance(TimeSpan.FromSeconds(30));

    Assert.True(limiter.TryAcquire(id, 1));
  }
}
