using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Seed;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class SkillSeederTests
{
  private static async Task<SkillSeeder> SetupAsync(Data.TietueDbContext db)
  {
    await new TypeSeeder(new TypeRepository(db)).SeedAsync();
    // EntityRepository WITHOUT a BehaviorDispatcher → no Qdrant embedding in tests.
    var entities = new EntityRepository(db, new SchemaValidator());
    return new SkillSeeder(db, entities);
  }

  [Fact]
  public async Task Seeds_all_standard_skills()
  {
    using var db = TestDb.New();
    var seeder = await SetupAsync(db);
    await seeder.SeedAsync();
    var count = await db.Entities.CountAsync(e => e.Type == "skill");
    Assert.Equal(12, count);
  }

  [Fact]
  public async Task Seeding_is_idempotent()
  {
    using var db = TestDb.New();
    var seeder = await SetupAsync(db);
    await seeder.SeedAsync();
    await seeder.SeedAsync();
    Assert.Equal(12, await db.Entities.CountAsync(e => e.Type == "skill"));
  }

  [Fact]
  public async Task Upsert_refreshes_changed_content()
  {
    using var db = TestDb.New();
    var seeder = await SetupAsync(db);
    await seeder.SeedAsync();
    var skill = await db.Entities.FirstAsync(e => e.Type == "skill");
    // Tamper with stored instructions, then re-seed and confirm it's restored.
    skill.Data = System.Text.Json.JsonSerializer.SerializeToDocument(new { name = ReadName(skill), description = "x", instructions = "tampered" });
    await db.SaveChangesAsync();
    await seeder.SeedAsync();
    var reloaded = await db.Entities.FirstAsync(e => e.Id == skill.Id);
    Assert.DoesNotContain("tampered", reloaded.Data.RootElement.GetProperty("instructions").GetString());
  }

  private static string ReadName(Data.Entity e)
  {
    return e.Data.RootElement.GetProperty("name").GetString()!;
  }
}
