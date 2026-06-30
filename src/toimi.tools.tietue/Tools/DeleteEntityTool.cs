using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Entities;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class DeleteEntityTool(EntityRepository repository)
{
  [McpServerTool, Description("Delete an entity by id.")]
  public async Task<string> Delete(
      [Description("The entity id (GUID)")] string id)
  {
    if (!Guid.TryParse(id, out var guid))
    {
      return "Invalid id format. Expected a GUID.";
    }

    var deleted = await repository.DeleteAsync(guid);
    return deleted ? $"Entity '{id}' deleted." : $"Entity '{id}' not found.";
  }
}
