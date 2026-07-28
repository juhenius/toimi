using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using toimi.tools.tietue.Data;
using Xunit;

namespace toimi.tools.tietue.Tests;

public static class TestConfig
{
  /// <summary>A minimal config for repos that inject <see cref="Toimi.Core.Configuration.ToimiConfiguration"/>.</summary>
  public static readonly Toimi.Core.Configuration.ToimiConfiguration Default = new()
  {
    OpenAI = new Toimi.Core.Configuration.OpenAIOptions { ApiKey = "test" },
    UserTimeZone = "Europe/Helsinki",
  };
}

public static class TestDb
{
  public static TietueDbContext New()
  {
    return new(Options());
  }

  /// <summary>A fresh context over the same InMemory store as <paramref name="db"/>.</summary>
  public static TietueDbContext SameStore(TietueDbContext db)
  {
    return new((DbContextOptions<TietueDbContext>)db.GetService<IDbContextOptions>());
  }

  /// <summary>
  /// A context whose next SaveChangesAsync (once <see cref="ThrowOnceDbContext.ThrowNext"/> is set)
  /// throws a DbUpdateException with an inner PostgresException(SqlState 23505), driving the
  /// unique-index catch paths the InMemory provider can never reach on its own.
  /// </summary>
  public static ThrowOnceDbContext NewThrowingOnce()
  {
    return new(Options(), new Npgsql.PostgresException(
      "duplicate key value violates unique constraint", "ERROR", "ERROR", "23505"));
  }

  private static DbContextOptions<TietueDbContext> Options()
  {
    return new DbContextOptionsBuilder<TietueDbContext>()
      .UseInMemoryDatabase($"tietue-{Guid.NewGuid()}")
      .Options;
  }
}

// The EF InMemory provider does not enforce unique indexes, so DbUpdateException
// catch paths are unreachable unless forced: throw once on demand.
public sealed class ThrowOnceDbContext(DbContextOptions<TietueDbContext> options, Exception? inner = null) : TietueDbContext(options)
{
  public bool ThrowNext { get; set; }

  public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
  {
    if (ThrowNext)
    {
      ThrowNext = false;
      throw new DbUpdateException("simulated unique-index collision", inner);
    }

    return base.SaveChangesAsync(cancellationToken);
  }
}

public class DbContextTests
{
  [Fact]
  public async Task Entity_round_trips_with_jsonb_data()
  {
    using var db = TestDb.New();
    var id = Guid.NewGuid();
    db.Entities.Add(new Entity
    {
      Id = id,
      Type = "note",
      Data = JsonDocument.Parse("""{"title":"hello"}"""),
      Tags = ["a", "b"],
      CreatedAt = DateTimeOffset.UtcNow,
      UpdatedAt = DateTimeOffset.UtcNow,
    });
    await db.SaveChangesAsync();

    var loaded = await db.Entities.FindAsync(id);
    Assert.NotNull(loaded);
    Assert.Equal("note", loaded.Type);
    Assert.Equal("hello", loaded.Data.RootElement.GetProperty("title").GetString());
    Assert.Equal(["a", "b"], loaded.Tags);
  }

  [Fact]
  public async Task UniqueKey_round_trips()
  {
    using var db = TestDb.New();
    var entityId = Guid.NewGuid();
    db.Entities.Add(new Entity
    {
      Id = entityId,
      Type = "wishlist",
      Data = JsonDocument.Parse("""{"url":"x"}"""),
      CreatedAt = DateTimeOffset.UtcNow,
      UpdatedAt = DateTimeOffset.UtcNow,
    });
    db.UniqueKeys.Add(new UniqueKey { Type = "wishlist", Field = "url", Value = "x", EntityId = entityId });
    await db.SaveChangesAsync();

    var loaded = await db.UniqueKeys.SingleAsync();
    Assert.Equal("wishlist", loaded.Type);
    Assert.Equal("url", loaded.Field);
    Assert.Equal("x", loaded.Value);
    Assert.Equal(entityId, loaded.EntityId);
  }
}
