using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Entities;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class GetEntityTool(EntityRepository repository)
{
  [McpServerTool, Description("Get a single entity by id.")]
  public async Task<string> Get(
      [Description("The entity id (GUID)")] string id)
  {
    if (!Guid.TryParse(id, out var guid))
    {
      return "Invalid id format. Expected a GUID.";
    }

    var e = await repository.GetAsync(guid);
    return e is null ? $"Entity '{id}' not found." : ToolHelpers.Render(e);
  }
}
