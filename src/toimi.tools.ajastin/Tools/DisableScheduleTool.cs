using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.ajastin.Data;

namespace toimi.tools.ajastin.Tools;

[McpServerToolType]
public class DisableScheduleTool(ScheduleRepository repository)
{
  [McpServerTool, Description("Disable a scheduled task by name.")]
  public async Task<string> DisableSchedule(
    [Description("Name of the schedule to disable")] string name)
  {
    var schedule = await repository.GetByNameAsync(name);
    if (schedule == null)
    {
      return "Schedule not found.";
    }

    if (!schedule.Enabled)
    {
      return $"Schedule '{name}' is already disabled.";
    }

    schedule.Enabled = false;
    await repository.UpdateAsync(schedule);
    return $"Schedule '{name}' disabled.";
  }
}
