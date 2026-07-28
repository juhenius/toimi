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
}
