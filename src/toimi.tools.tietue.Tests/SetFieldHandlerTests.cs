using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class SetFieldHandlerTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"status":{"type":"string"}}}""";

  private static SetFieldHandler Handler()
  {
    return new SetFieldHandler(new EntityRepository(TestDb.New(), new SchemaValidator()));
  }

  [Fact]
  public async Task Sets_a_data_field_via_repository()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("task", Schema);
    var repo = new EntityRepository(db, new SchemaValidator());
    var e = await repo.CreateAsync("task", JsonNode.Parse("""{"status":"open"}"""), []);

    var handler = new SetFieldHandler(repo);
    var result = await handler.HandleAsync(new HandlerContext(e, /*lang=json,strict*/ """{"path":"status","value":"done"}""", DateTimeOffset.UtcNow));

    var reloaded = await repo.GetAsync(e.Id);
    Assert.Equal("done", reloaded!.Data.RootElement.GetProperty("status").GetString());
    Assert.Equal("applied", result.Status);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("{}")]
  [InlineData(/*lang=json,strict*/ """{"value":1}""")]
  public void ValidateConfig_rejects_configs_without_a_path(string? config)
  {
    Assert.False(Handler().ValidateConfig(config).IsValid);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"path":"status","value":"done"}""")]
  [InlineData(/*lang=json,strict*/ """{"path":"status"}""")]
  public void ValidateConfig_accepts_a_path_with_or_without_value(string config)
  {
    // A missing value is a genuine null-assignment, not an error.
    Assert.True(Handler().ValidateConfig(config).IsValid);
  }
}
