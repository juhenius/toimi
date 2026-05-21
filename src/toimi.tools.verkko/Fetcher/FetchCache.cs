using System.Collections.Concurrent;

namespace toimi.tools.verkko.Fetcher;

public class FetchCache
{
  private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
  private readonly ConcurrentDictionary<string, (FetchResult Result, DateTime ExpiresAt)> _cache = new();

  public FetchResult? Get(string url)
  {
    return _cache.TryGetValue(url, out var entry) && entry.ExpiresAt > DateTime.UtcNow ? entry.Result : null;
  }

  public void Set(string url, FetchResult result)
  {
    _cache[url] = (result, DateTime.UtcNow + Ttl);

    // Clean expired entries
    foreach (var key in _cache.Keys)
    {
      if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt <= DateTime.UtcNow)
      {
        _cache.TryRemove(key, out _);
      }
    }
  }
}
