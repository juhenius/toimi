using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class DeleteTriggerTool(TriggerRepository repository)
{
  [McpServerTool, Description("Delete a trigger by id.")]
  public async Task<string> DeleteTrigger(
      [Description("Trigger id (GUID)")] string id)
  {
    return !Guid.TryParse(id, out var triggerId)
      ? "Invalid id. Expected a GUID."
      : await repository.DeleteAsync(triggerId) ? $"Trigger '{id}' deleted." : $"Trigger '{id}' not found.";
  }
}
