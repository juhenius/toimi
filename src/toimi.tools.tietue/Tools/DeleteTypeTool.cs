using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Types;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class DeleteTypeTool(TypeRepository repository)
{
  [McpServerTool, Description("Delete a data type by name. Does not delete existing entities of that type.")]
  public async Task<string> DeleteType(
      [Description("The type name")] string name)
  {
    var deleted = await repository.DeleteAsync(name);
    return deleted ? $"Type '{name}' deleted." : $"Type '{name}' not found.";
  }
}
