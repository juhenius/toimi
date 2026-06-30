using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using Xunit;

namespace toimi.tools.tietue.Tests;

public static class TestDb
{
  public static TietueDbContext New()
  {
    return new(new DbContextOptionsBuilder<TietueDbContext>()
      .UseInMemoryDatabase($"tietue-{Guid.NewGuid()}")
      .Options);
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
