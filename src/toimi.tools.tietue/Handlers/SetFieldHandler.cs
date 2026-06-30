using System.Text.Json;
using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;

namespace toimi.tools.tietue.Handlers;

public class SetFieldHandler(EntityRepository repository) : INativeHandler
{
  public string Kind => "set-field";

  public async Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default)
  {
    using var cfg = JsonDocument.Parse(ctx.ConfigJson ?? "{}");
    var path = cfg.RootElement.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    if (string.IsNullOrEmpty(path))
    {
      return new HandlerResult("skipped", /*lang=json,strict*/ """{"reason":"no path"}""");
    }

    var value = cfg.RootElement.TryGetProperty("value", out var v) ? JsonNode.Parse(v.GetRawText()) : null;

    var data = JsonNode.Parse(ctx.Entity.Data.RootElement.GetRawText())!.AsObject();
    data[path] = value;
    await repository.UpdateAsync(ctx.Entity.Id, data, null, ct);

    return new HandlerResult("applied", $$"""{"path":"{{path}}"}""");
  }
}
