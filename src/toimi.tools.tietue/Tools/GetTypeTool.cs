using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Types;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class GetTypeTool(TypeRepository repository)
{
  [McpServerTool, Description("Get a single data type and its JSON Schema by name.")]
  public async Task<string> GetType(
      [Description("The type name")] string name)
  {
    var t = await repository.GetAsync(name);
    return t is null
      ? $"Type '{name}' not found."
      : JsonSerializer.Serialize(new
      {
        t.Name,
        Schema = JsonDocument.Parse(t.JsonSchema.RootElement.GetRawText()),
      });
  }
}
