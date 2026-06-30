using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Types;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class ListTypesTool(TypeRepository repository)
{
  [McpServerTool, Description("List all defined data types with their JSON Schemas. Use this to discover what types exist and how to shape their data before creating entities.")]
  public async Task<string> ListTypes()
  {
    var types = await repository.ListAsync();
    var rows = types.Select(t => new JsonObject
    {
      ["name"] = t.Name,
      ["schema"] = JsonNode.Parse(t.JsonSchema.RootElement.GetRawText()),
    }).ToArray();
    return JsonSerializer.Serialize(new JsonArray(rows));
  }
}
