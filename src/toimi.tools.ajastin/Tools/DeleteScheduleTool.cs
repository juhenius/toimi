using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.ajastin.Data;

namespace toimi.tools.ajastin.Tools;

[McpServerToolType]
public class DeleteScheduleTool(ScheduleRepository repository)
{
  [McpServerTool, Description("Delete a scheduled task by name.")]
  public async Task<string> DeleteSchedule(
    [Description("Name of the schedule to delete")] string name)
  {
    var schedule = await repository.GetByNameAsync(name);
    if (schedule == null)
    {
      return "Schedule not found.";
    }

    await repository.DeleteAsync(schedule.Id);
    return $"Schedule '{name}' deleted.";
  }
}
