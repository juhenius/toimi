using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Semantic;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class OutboxWorkerTests
{
  private sealed class FailingIndex : ISemanticIndex
  {
    public int Calls { get; private set; }
    public bool Fail { get; set; } = true;

    /// <summary>When &gt; 0, the first N index/remove calls throw; subsequent calls succeed.</summary>
    public int FailFirstN { get; set; }

    public Task EnsureCollectionAsync(string collection, CancellationToken ct = default)
    {
      return Task.CompletedTask;
    }

    public Task IndexAsync(string collection, Guid entityId, string text, CancellationToken ct = default)
    {
      Calls++;
      return ShouldFail() ? throw new InvalidOperationException("down") : Task.CompletedTask;
    }

    public Task RemoveAsync(string collection, Guid entityId, CancellationToken ct = default)
    {
      Calls++;
      return ShouldFail() ? throw new InvalidOperationException("down") : Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScoredId>> SearchAsync(string collection, string query, int limit, CancellationToken ct = default)
    {
      return Task.FromResult<IReadOnlyList<ScoredId>>([]);
    }

    public Task<IReadOnlyList<Guid>> ListIdsAsync(string collection, CancellationToken ct = default)
    {
      return Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private bool ShouldFail()
    {
      return FailFirstN > 0 ? Calls <= FailFirstN : Fail;
    }
  }

  private static IndexOutbox Row(int attempts, DateTimeOffset? lastAttempt, DateTimeOffset created)
  {
    return new IndexOutbox
    {
      Id = Guid.NewGuid(),
      EntityId = Guid.NewGuid(),
      Type = "memory",
      Op = "delete", // delete ops need no entity/typedef rows — simplest to drive the worker with
      Attempts = attempts,
      LastAttemptAt = lastAttempt,
      CreatedAt = created,
    };
  }

  [Fact]
  public async Task Retries_due_row_and_removes_on_success()
  {
    using var db = TestDb.New();
    var index = new FailingIndex { Fail = false };
    var now = DateTimeOffset.UtcNow;
    db.IndexOutbox.Add(Row(attempts: 1, lastAttempt: now.AddMinutes(-5), created: now.AddMinutes(-6)));
    await db.SaveChangesAsync();

    await OutboxWorker.RunOnceAsync(db, new SemanticOutbox(db, index), now, default);

    Assert.Equal(1, index.Calls);
    Assert.Empty(await db.IndexOutbox.ToListAsync());
  }

  [Fact]
  public async Task Backoff_skips_rows_attempted_too_recently()
  {
    using var db = TestDb.New();
    var index = new FailingIndex();
    var now = DateTimeOffset.UtcNow;
    // Attempts=3 → backoff 2^3 = 8 minutes; last attempt 1 minute ago → not due.
    db.IndexOutbox.Add(Row(attempts: 3, lastAttempt: now.AddMinutes(-1), created: now.AddHours(-1)));
    await db.SaveChangesAsync();

    await OutboxWorker.RunOnceAsync(db, new SemanticOutbox(db, index), now, default);

    Assert.Equal(0, index.Calls);
  }

  [Fact]
  public async Task Failure_increments_attempts_and_records_error()
  {
    using var db = TestDb.New();
    var index = new FailingIndex();
    var now = DateTimeOffset.UtcNow;
    db.IndexOutbox.Add(Row(attempts: 1, lastAttempt: now.AddMinutes(-5), created: now.AddMinutes(-6)));
    await db.SaveChangesAsync();

    await OutboxWorker.RunOnceAsync(db, new SemanticOutbox(db, index), now, default);

    var row = await db.IndexOutbox.SingleAsync();
    Assert.Equal(2, row.Attempts);
    Assert.Contains("down", row.LastError);
  }

  [Fact]
  public async Task Dead_rows_are_left_alone()
  {
    using var db = TestDb.New();
    var index = new FailingIndex();
    var now = DateTimeOffset.UtcNow;
    db.IndexOutbox.Add(Row(attempts: SemanticOutbox.MaxAttempts, lastAttempt: now.AddDays(-1), created: now.AddDays(-2)));
    await db.SaveChangesAsync();

    await OutboxWorker.RunOnceAsync(db, new SemanticOutbox(db, index), now, default);

    Assert.Equal(0, index.Calls);
    Assert.Equal(SemanticOutbox.MaxAttempts, (await db.IndexOutbox.SingleAsync()).Attempts);
  }

  [Fact]
  public async Task Undrained_fresh_row_waits_for_grace_period()
  {
    using var db = TestDb.New();
    var index = new FailingIndex { Fail = false };
    var now = DateTimeOffset.UtcNow;
    // Attempts=0 (inline drain never ran — e.g. pod died post-commit): picked up only after 2 min grace.
    db.IndexOutbox.Add(Row(attempts: 0, lastAttempt: null, created: now.AddSeconds(-30)));
    db.IndexOutbox.Add(Row(attempts: 0, lastAttempt: null, created: now.AddMinutes(-5)));
    await db.SaveChangesAsync();

    await OutboxWorker.RunOnceAsync(db, new SemanticOutbox(db, index), now, default);

    Assert.Equal(1, index.Calls); // only the 5-minute-old row
    Assert.Single(await db.IndexOutbox.ToListAsync());
  }

  [Fact]
  public async Task Mixed_batch_processes_later_rows_after_an_earlier_failure()
  {
    using var db = TestDb.New();
    // Fail only the first index call; the second row's call should succeed.
    var index = new FailingIndex { FailFirstN = 1 };
    var now = DateTimeOffset.UtcNow;
    var failRow = Row(attempts: 1, lastAttempt: now.AddMinutes(-5), created: now.AddMinutes(-6));
    var succeedRow = Row(attempts: 1, lastAttempt: now.AddMinutes(-5), created: now.AddMinutes(-6));
    db.IndexOutbox.Add(failRow);
    db.IndexOutbox.Add(succeedRow);
    await db.SaveChangesAsync();

    await OutboxWorker.RunOnceAsync(db, new SemanticOutbox(db, index), now, default);

    // The failed row stays, attempts bumped and error recorded.
    var remaining = await db.IndexOutbox.ToListAsync();
    var survived = Assert.Single(remaining);
    Assert.Equal(2, survived.Attempts);
    Assert.Contains("down", survived.LastError);

    // The successful row was removed.
    Assert.Equal(2, index.Calls);
  }

  [Fact]
  public async Task Due_row_behind_a_wall_of_backoff_rows_still_processes()
  {
    using var db = TestDb.New();
    var index = new FailingIndex { Fail = false };
    var now = DateTimeOffset.UtcNow;
    for (var i = 0; i < 25; i++)
    {
      // In backoff (attempts 3 → 8-min backoff, attempted 1 min ago), older than the due row.
      db.IndexOutbox.Add(Row(attempts: 3, lastAttempt: now.AddMinutes(-1), created: now.AddHours(-2)));
    }

    db.IndexOutbox.Add(Row(attempts: 1, lastAttempt: now.AddMinutes(-5), created: now.AddMinutes(-10)));
    await db.SaveChangesAsync();

    var processed = await OutboxWorker.RunOnceAsync(db, new SemanticOutbox(db, index), now, default);

    Assert.Equal(1, processed);
  }

  [Fact]
  public async Task Attempted_row_with_null_last_attempt_is_immediately_due()
  {
    using var db = TestDb.New();
    var index = new FailingIndex { Fail = false };
    var now = DateTimeOffset.UtcNow;
    // Attempts=1 but LastAttemptAt is null: IsDue should treat this as due immediately.
    db.IndexOutbox.Add(Row(attempts: 1, lastAttempt: null, created: now));
    await db.SaveChangesAsync();

    await OutboxWorker.RunOnceAsync(db, new SemanticOutbox(db, index), now, default);

    Assert.Equal(1, index.Calls);
    Assert.Empty(await db.IndexOutbox.ToListAsync());
  }
}
