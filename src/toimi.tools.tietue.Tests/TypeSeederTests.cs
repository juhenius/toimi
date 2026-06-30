using toimi.tools.tietue.Seed;
using toimi.tools.tietue.Types;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TypeSeederTests
{
  [Fact]
  public async Task Seeds_memory_and_skill_types_with_semantic_index()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    await new TypeSeeder(repo).SeedAsync();

    var memory = await repo.GetAsync("memory");
    var skill = await repo.GetAsync("skill");
    Assert.NotNull(memory);
    Assert.NotNull(skill);
    Assert.Contains("SemanticIndex", memory.Behaviors);
    Assert.Contains("Expiry", memory.Behaviors);
    Assert.Contains("SemanticIndex", skill.Behaviors);
    Assert.Contains("UniqueName", skill.Behaviors);
  }

  [Fact]
  public async Task Seeding_twice_is_idempotent()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    await new TypeSeeder(repo).SeedAsync();
    await new TypeSeeder(repo).SeedAsync();

    Assert.Equal(4, (await repo.ListAsync()).Count);
  }

  [Fact]
  public async Task Seeds_reminder_with_default_notify_trigger()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    await new TypeSeeder(repo).SeedAsync();

    var reminder = await repo.GetAsync("reminder");
    Assert.NotNull(reminder);
    Assert.Contains("notify", reminder.DefaultTriggers);
    Assert.Contains("dueAt", reminder.DefaultTriggers);
  }

  [Fact]
  public async Task Seeds_schedule_with_default_message_trigger()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    await new TypeSeeder(repo).SeedAsync();

    var schedule = await repo.GetAsync("schedule");
    Assert.NotNull(schedule);
    Assert.Contains("message", schedule.DefaultTriggers);
    Assert.Contains("prompt", schedule.DefaultTriggers);
  }
}
