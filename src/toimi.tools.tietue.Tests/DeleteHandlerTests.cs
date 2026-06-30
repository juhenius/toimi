using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class DeleteHandlerTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"name":{"type":"string"}}}""";

  [Fact]
  public async Task Deletes_the_entity()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("task", Schema);
    var repo = new EntityRepository(db, new SchemaValidator());
    var e = await repo.CreateAsync("task", JsonNode.Parse("""{"name":"x"}"""), []);

    var handler = new DeleteHandler(repo);
    var result = await handler.HandleAsync(new HandlerContext(e, null, DateTimeOffset.UtcNow));

    Assert.Equal("deleted", result.Status);
    Assert.Null(await repo.GetAsync(e.Id));
  }

  [Fact]
  public async Task Reports_skipped_when_already_gone()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("task", Schema);
    var repo = new EntityRepository(db, new SchemaValidator());
    var e = await repo.CreateAsync("task", JsonNode.Parse("""{"name":"x"}"""), []);
    await repo.DeleteAsync(e.Id);

    var handler = new DeleteHandler(repo);
    var result = await handler.HandleAsync(new HandlerContext(e, null, DateTimeOffset.UtcNow));

    Assert.Equal("skipped", result.Status);
  }
}
