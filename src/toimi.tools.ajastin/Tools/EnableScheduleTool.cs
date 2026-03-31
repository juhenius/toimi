using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.ajastin.Data;

namespace toimi.tools.ajastin.Tools;

[McpServerToolType]
public class EnableScheduleTool(ScheduleRepository repository)
{
  [McpServerTool, Description("Enable a scheduled task by name.")]
  public async Task<string> EnableSchedule(
    [Description("Name of the schedule to enable")] string name)
  {
    var schedule = await repository.GetByNameAsync(name);
    if (schedule == null)
    {
      return "Schedule not found.";
    }

    if (schedule.Enabled)
    {
      return $"Schedule '{name}' is already enabled.";
    }

    schedule.Enabled = true;
    await repository.UpdateAsync(schedule);
    return $"Schedule '{name}' enabled.";
  }
}
