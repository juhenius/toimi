using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Scheduling;

public sealed class PostgresTickLock(TietueDbContext db, ILogger<PostgresTickLock>? logger = null) : ITickLock
{
  // Advisory locks are per-database, so the key only needs to be unique within the tietue DB.
  private const long LockKey = 7415011;

  public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
  {
    // Advisory locks are session-scoped: keep the connection open for the lease's
    // lifetime so the lock is held until released. EF ref-counts explicit opens,
    // so queries issued during the tick reuse this same connection/session.
    await db.Database.OpenConnectionAsync(ct);
    bool acquired;
    try
    {
      acquired = await ExecuteBoolAsync(db, $"SELECT pg_try_advisory_lock({LockKey})", ct);
    }
    catch
    {
      // If cancellation lands after Postgres granted the lock, the pooled connection may
      // briefly keep holding it until reuse/idle-pruning resets the session — ticks are
      // then skipped (Debug log) for at most a few minutes. Self-healing, no action needed.
      await db.Database.CloseConnectionAsync();
      throw;
    }

    if (!acquired)
    {
      await db.Database.CloseConnectionAsync();
      return null;
    }

    return new Lease(db, logger);
  }

  private static async Task<bool> ExecuteBoolAsync(TietueDbContext db, string sql, CancellationToken ct)
  {
    var connection = db.Database.GetDbConnection();
    using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private sealed class Lease(TietueDbContext db, ILogger? logger) : IAsyncDisposable
  {
    public async ValueTask DisposeAsync()
    {
      try
      {
        await ExecuteBoolAsync(db, $"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
      }
      catch (Exception ex)
      {
        // A failed unlock means the session is broken; Postgres releases advisory locks
        // with the session, so swallowing here cannot leak the lock — and rethrowing
        // would mask the tick's root-cause exception during await-using unwind.
        logger?.LogWarning(ex, "Advisory unlock failed; relying on session death to release the tick lock.");
      }
      finally
      {
        try
        {
          await db.Database.CloseConnectionAsync();
        }
        catch (Exception ex)
        {
          logger?.LogWarning(ex, "Closing the tick-lock connection failed; the pool will discard it.");
        }
      }
    }
  }
}
