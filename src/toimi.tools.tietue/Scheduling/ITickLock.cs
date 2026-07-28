namespace toimi.tools.tietue.Scheduling;

/// <summary>
/// Serializes scheduler ticks across concurrent tietue instances (e.g. the
/// overlap window during a deploy) so a due trigger fires exactly once.
/// </summary>
public interface ITickLock
{
  /// <summary>Returns a lease to dispose when done, or null when another instance holds the lock.</summary>
  Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct);
}
