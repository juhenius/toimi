using toimi.tools.tietue.Entities;

namespace toimi.tools.tietue.Handlers;

public class DeleteHandler(EntityRepository repository) : INativeHandler
{
  public string Kind => "delete";

  public async Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default)
  {
    var deleted = await repository.DeleteAsync(ctx.Entity.Id, ct);
    return deleted
      ? new HandlerResult("deleted")
      : new HandlerResult("skipped", /*lang=json,strict*/ """{"reason":"not found"}""");
  }
}
