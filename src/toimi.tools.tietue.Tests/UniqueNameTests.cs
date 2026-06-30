using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class UniqueNameTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"url":{"type":"string"},"title":{"type":"string"}}}""";
  private const string UniqueOnUrl = /*lang=json,strict*/ """[{"behavior":"UniqueName","config":{"field":"url"}}]""";

  private static async Task<EntityRepository> SetupAsync(Data.TietueDbContext db, string? behaviors)
  {
    await new TypeRepository(db).DefineAsync("wishlist", Schema, behaviors);
    return new EntityRepository(db, new SchemaValidator());
  }

  [Fact]
  public async Task Rejects_second_entity_with_same_keyed_value()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, UniqueOnUrl);
    await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a","title":"one"}"""), []);

    await Assert.ThrowsAsync<TietueValidationException>(() =>
      repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a","title":"two"}"""), []));
  }

  [Fact]
  public async Task Allows_distinct_keyed_values()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, UniqueOnUrl);
    await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a"}"""), []);
    var second = await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"b"}"""), []);
    Assert.NotNull(second);
  }

  [Fact]
  public async Task No_constraint_without_unique_name_behavior()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, null);
    await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a"}"""), []);
    var second = await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a"}"""), []);
    Assert.NotNull(second);
  }

  [Fact]
  public async Task Missing_keyed_field_is_not_enforced()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, UniqueOnUrl);
    await repo.CreateAsync("wishlist", JsonNode.Parse("""{"title":"one"}"""), []);
    var second = await repo.CreateAsync("wishlist", JsonNode.Parse("""{"title":"two"}"""), []);
    Assert.NotNull(second);
  }

  [Fact]
  public async Task Update_into_existing_value_is_rejected()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, UniqueOnUrl);
    await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a"}"""), []);
    var b = await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"b"}"""), []);

    await Assert.ThrowsAsync<TietueValidationException>(() =>
      repo.UpdateAsync(b.Id, JsonNode.Parse("""{"url":"a"}"""), null));
  }

  [Fact]
  public async Task Updating_own_value_frees_the_old_one()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, UniqueOnUrl);
    var a = await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a"}"""), []);

    await repo.UpdateAsync(a.Id, JsonNode.Parse("""{"url":"c"}"""), null);

    // "a" is now free to reuse
    var reused = await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a"}"""), []);
    Assert.NotNull(reused);
  }

  [Fact]
  public async Task Delete_frees_the_keyed_value()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, UniqueOnUrl);
    var a = await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a"}"""), []);

    await repo.DeleteAsync(a.Id);

    var recreated = await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a"}"""), []);
    Assert.NotNull(recreated);
  }
}
