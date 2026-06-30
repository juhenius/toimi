using System.Text.Json;
using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Seed;
using toimi.tools.tietue.Tools;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ListSkillsToolTests
{
  [Fact]
  public async Task Lists_name_and_description_for_skill_entities()
  {
    using var db = TestDb.New();
    await new TypeSeeder(new TypeRepository(db)).SeedAsync();
    var entities = new EntityRepository(db, new SchemaValidator());
    await entities.CreateAsync("skill", JsonNode.Parse("""{"name":"s1","description":"d1","instructions":"i1"}"""), []);

    var json = await new ListSkillsTool(db).ListSkills();
    using var doc = JsonDocument.Parse(json);
    var first = doc.RootElement.EnumerateArray().Single();
    Assert.Equal("s1", first.GetProperty("name").GetString());
    Assert.Equal("d1", first.GetProperty("description").GetString());
  }

  [Fact]
  public async Task Empty_array_when_no_skills()
  {
    using var db = TestDb.New();
    await new TypeSeeder(new TypeRepository(db)).SeedAsync();
    var json = await new ListSkillsTool(db).ListSkills();
    using var doc = JsonDocument.Parse(json);
    Assert.Empty(doc.RootElement.EnumerateArray());
  }
}
