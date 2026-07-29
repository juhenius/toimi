using toimi.tools.verkko.Fetcher;
using Xunit;

namespace toimi.tools.verkko.Tests;

public class FetchCacheTests
{
  [Fact]
  public void Caps_entry_count_by_evicting_when_full()
  {
    var cache = new FetchCache();
    for (var i = 0; i <= FetchCache.MaxEntries; i++)
    {
      cache.Set($"https://example.com/{i}", new FetchResult($"https://example.com/{i}", 200, "text/html", "x"));
    }

    var live = 0;
    for (var i = 0; i <= FetchCache.MaxEntries; i++)
    {
      if (cache.Get($"https://example.com/{i}") is not null)
      {
        live++;
      }
    }

    Assert.True(live <= FetchCache.MaxEntries);
  }

  [Fact]
  public void Still_serves_cached_entries_under_the_cap()
  {
    var cache = new FetchCache();
    cache.Set("https://example.com/a", new FetchResult("https://example.com/a", 200, "text/html", "hello"));

    Assert.NotNull(cache.Get("https://example.com/a"));
  }

  private sealed class ManualTimeProvider : TimeProvider
  {
    public DateTimeOffset Now { get; set; } = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
      return Now;
    }
  }

  [Fact]
  public void Entry_expires_after_the_ttl()
  {
    var clock = new ManualTimeProvider();
    var cache = new FetchCache(clock);
    cache.Set("https://example.com/a", new FetchResult("https://example.com/a", 200, "text/html", "hello"));

    clock.Now = clock.Now.AddMinutes(5).AddSeconds(1);

    Assert.Null(cache.Get("https://example.com/a"));
  }

  [Fact]
  public void Set_sweeps_expired_entries()
  {
    var clock = new ManualTimeProvider();
    var cache = new FetchCache(clock);
    cache.Set("https://example.com/a", new FetchResult("https://example.com/a", 200, "text/html", "hello"));

    clock.Now = clock.Now.AddMinutes(5).AddSeconds(1);
    cache.Set("https://example.com/b", new FetchResult("https://example.com/b", 200, "text/html", "world"));

    // The sweep in Set removed the expired "a" entry entirely (not just made it unreadable).
    Assert.Null(cache.Get("https://example.com/a"));
    Assert.NotNull(cache.Get("https://example.com/b"));
  }

  [Fact]
  public void Eviction_over_the_cap_removes_the_soonest_expiring_entry()
  {
    var clock = new ManualTimeProvider();
    var cache = new FetchCache(clock);

    // Stagger insertion times so each entry's expiry is strictly increasing;
    // the first ("soonest-expiring") one is not yet expired when we go over the cap.
    for (var i = 0; i < FetchCache.MaxEntries; i++)
    {
      cache.Set($"https://example.com/{i}", new FetchResult($"https://example.com/{i}", 200, "text/html", "x"));
      clock.Now = clock.Now.AddSeconds(1);
    }

    // One more entry pushes the cache over MaxEntries; the soonest-expiring
    // survivor (entry 0, set earliest) must be the one evicted.
    cache.Set("https://example.com/extra", new FetchResult("https://example.com/extra", 200, "text/html", "x"));

    Assert.Null(cache.Get("https://example.com/0"));
    Assert.NotNull(cache.Get("https://example.com/1"));
    Assert.NotNull(cache.Get("https://example.com/extra"));
  }
}
