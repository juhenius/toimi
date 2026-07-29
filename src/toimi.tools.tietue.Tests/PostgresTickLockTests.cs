using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Scheduling;
using Xunit;

namespace toimi.tools.tietue.Tests;

// Per-test container lifecycle, deliberately: xUnit v2 instantiates the test class once
// per test method, so IAsyncLifetime here starts one container per test (three total,
// ~1-2s each after the first image pull). Do NOT "optimize" this into an IClassFixture —
// a skipped [DockerFact] never constructs the class, so on a docker-less machine no
// container start is ever attempted; a class fixture would initialize (and fail) even
// when every test in the class is skipped.
public class PostgresTickLockTests : IAsyncLifetime
{
  private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
    .Build();

  public Task InitializeAsync()
  {
    return _postgres.StartAsync();
  }

  public Task DisposeAsync()
  {
    return _postgres.DisposeAsync().AsTask();
  }

  // A context per lock instance: advisory locks are SESSION-scoped, so two locks must
  // not share a connection or the second would trivially "already hold" the lock.
  private TietueDbContext NewContext()
  {
    return new TietueDbContext(new DbContextOptionsBuilder<TietueDbContext>()
      .UseNpgsql(_postgres.GetConnectionString())
      .Options);
  }

  [DockerFact]
  public async Task Second_acquire_is_refused_while_the_lease_is_held()
  {
    using var dbA = NewContext();
    using var dbB = NewContext();

    var leaseA = await new PostgresTickLock(dbA).TryAcquireAsync(default);
    Assert.NotNull(leaseA);

    // This is the property the whole scheduler design rests on: a second replica's tick
    // is refused, which is what makes EntityEventStore's read-modify-write stale-claim
    // take-over safe (see its comment).
    Assert.Null(await new PostgresTickLock(dbB).TryAcquireAsync(default));

    await leaseA.DisposeAsync();
  }

  [DockerFact]
  public async Task Lock_is_released_when_the_lease_is_disposed()
  {
    using var dbA = NewContext();
    using var dbB = NewContext();

    var leaseA = await new PostgresTickLock(dbA).TryAcquireAsync(default);
    Assert.NotNull(leaseA);
    await leaseA.DisposeAsync();

    var leaseB = await new PostgresTickLock(dbB).TryAcquireAsync(default);
    Assert.NotNull(leaseB);
    await leaseB.DisposeAsync();
  }

  [DockerFact]
  public async Task Queries_issued_during_the_lease_keep_holding_the_lock()
  {
    using var dbA = NewContext();
    using var dbB = NewContext();

    var leaseA = await new PostgresTickLock(dbA).TryAcquireAsync(default);
    Assert.NotNull(leaseA);

    // EF ref-counts the explicit OpenConnection, so work done during the tick reuses the
    // same session. If it silently opened a second connection, the advisory lock would
    // live on a session that closes early and a second replica could tick concurrently.
    await dbA.Database.ExecuteSqlRawAsync("SELECT 1");
    Assert.Null(await new PostgresTickLock(dbB).TryAcquireAsync(default));

    await leaseA.DisposeAsync();
    var leaseB = await new PostgresTickLock(dbB).TryAcquireAsync(default);
    Assert.NotNull(leaseB);
    await leaseB.DisposeAsync();
  }
}
