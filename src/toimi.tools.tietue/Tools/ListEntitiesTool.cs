using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Entities;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class ListEntitiesTool(EntityRepository repository)
{
  [McpServerTool, Description("List entities, optionally filtered by type and/or a single tag, with paging.")]
  public async Task<string> List(
      [Description("Optional type name to filter by")] string? type = null,
      [Description("Optional single tag to filter by")] string? tag = null,
      [Description("Page number (1-based, default 1)")] int page = 1,
      [Description("Page size (default 20, max 100)")] int size = 20)
  {
    var result = await repository.ListAsync(type, tag, page, size);
    var items = result.Items.Select(e => new JsonObject
    {
      ["id"] = e.Id.ToString(),
      ["type"] = e.Type,
      ["data"] = JsonNode.Parse(e.Data.RootElement.GetRawText()),
      ["tags"] = new JsonArray(e.Tags.Select(t => (JsonNode)t).ToArray()),
      ["updatedAt"] = e.UpdatedAt.ToString("o"),
    }).ToArray();

    return JsonSerializer.Serialize(new JsonObject
    {
      ["items"] = new JsonArray(items),
      ["page"] = result.Page,
      ["size"] = result.Size,
      ["total"] = result.Total,
    });
  }
}
