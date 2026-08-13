using System.Collections.Concurrent;

namespace toimi.tools.tietue.Webhooks;

/// <summary>
/// Fixed-window per-webhook rate limiting for /hooks. In-memory is correct:
/// tietue is replicas:1 (singleton scheduler, Recreate strategy) and a lost
/// window on restart only briefly under-counts.
/// </summary>
public class WebhookRateLimiter(TimeProvider? time = null)
{
  private sealed class Window
  {
    public int Count;
  }

  private readonly TimeProvider _time = time ?? TimeProvider.System;
  private readonly ConcurrentDictionary<(Guid TriggerId, long Minute), Window> _windows = new();

  /// <summary>Consumes one firing slot in the trigger's current minute window; false when over the limit.</summary>
  public bool TryAcquire(Guid triggerId, int limitPerMinute)
  {
    var minute = _time.GetUtcNow().UtcTicks / TimeSpan.TicksPerMinute;
    foreach (var (key, _) in _windows)
    {
      if (key.Minute < minute - 1)
      {
        _windows.TryRemove(key, out _);
      }
    }

    var window = _windows.GetOrAdd((triggerId, minute), _ => new Window());
    return Interlocked.Increment(ref window.Count) <= limitPerMinute;
  }
}
