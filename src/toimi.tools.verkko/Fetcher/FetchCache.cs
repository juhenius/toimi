using System.Collections.Concurrent;

namespace toimi.tools.verkko.Fetcher;

public class FetchCache(TimeProvider? time = null)
{
  public const int MaxEntries = 200;

  private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
  private readonly TimeProvider _time = time ?? TimeProvider.System;
  private readonly ConcurrentDictionary<string, (FetchResult Result, DateTime ExpiresAt)> _cache = new();

  public FetchResult? Get(string url)
  {
    return _cache.TryGetValue(url, out var entry) && entry.ExpiresAt > _time.GetUtcNow().UtcDateTime ? entry.Result : null;
  }

  public void Set(string url, FetchResult result)
  {
    _cache[url] = (result, _time.GetUtcNow().UtcDateTime + Ttl);

    // Clean expired entries
    foreach (var key in _cache.Keys)
    {
      if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt <= _time.GetUtcNow().UtcDateTime)
      {
        _cache.TryRemove(key, out _);
      }
    }

    // Bound memory: evict the soonest-expiring entries when over the cap.
    while (_cache.Count > MaxEntries)
    {
      var oldest = _cache.OrderBy(kv => kv.Value.ExpiresAt).First();
      _cache.TryRemove(oldest.Key, out _);
    }
  }
}
