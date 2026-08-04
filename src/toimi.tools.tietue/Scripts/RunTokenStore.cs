using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace toimi.tools.tietue.Scripts;

/// <summary>
/// One-time run tokens gating the extract() callback (spec §5.5). In-memory is
/// correct: tietue is replicas:1 (singleton scheduler) and a token only needs
/// to outlive its own script run.
/// </summary>
public class RunTokenStore(TimeProvider? time = null)
{
  public const int MaxExtractCalls = 3;

  private sealed class Entry(Guid entityId, string[] grants, DateTimeOffset expiresAt)
  {
    public Guid EntityId { get; } = entityId;
    public string[] Grants { get; } = grants;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public int Calls;
  }

  private readonly TimeProvider _time = time ?? TimeProvider.System;
  private readonly ConcurrentDictionary<string, Entry> _tokens = new();

  public string Issue(Guid entityId, string[] grants, TimeSpan ttl)
  {
    foreach (var (key, entry) in _tokens)
    {
      if (entry.ExpiresAt < _time.GetUtcNow())
      {
        _tokens.TryRemove(key, out _);
      }
    }

    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    _tokens[token] = new Entry(entityId, grants, _time.GetUtcNow() + ttl);
    return token;
  }

  /// <summary>Validates the token (exists, unexpired, llm grant, call budget) and consumes one extract call.</summary>
  public bool TryUseExtract(string token)
  {
    return _tokens.TryGetValue(token, out var entry)
      && entry.ExpiresAt >= _time.GetUtcNow()
      && entry.Grants.Contains("llm", StringComparer.OrdinalIgnoreCase)
      && Interlocked.Increment(ref entry.Calls) <= MaxExtractCalls;
  }

  public void Revoke(string token)
  {
    _tokens.TryRemove(token, out _);
  }
}
