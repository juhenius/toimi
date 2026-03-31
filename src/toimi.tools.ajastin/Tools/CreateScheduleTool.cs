using System.ComponentModel;
using System.Text.Json;
using Cronos;
using ModelContextProtocol.Server;
using toimi.tools.ajastin.Data;

namespace toimi.tools.ajastin.Tools;

[McpServerToolType]
public class CreateScheduleTool(ScheduleRepository repository)
{
  [McpServerTool, Description("Create a scheduled task that runs an AI prompt on a cron schedule. The prompt will be executed as a full agent session with access to all tools.")]
  public async Task<string> CreateSchedule(
    [Description("Short name for the schedule")] string name,
    [Description("Cron expression (5 fields: minute hour day month weekday, e.g. '0 7 * * *' for daily at 7am UTC)")] string cronExpression,
    [Description("The prompt to execute on each run")] string prompt)
  {
    try
    {
      CronExpression.Parse(cronExpression);
    }
    catch (CronFormatException ex)
    {
      return $"Invalid cron expression: {ex.Message}";
    }

    var existing = await repository.GetByNameAsync(name);
    if (existing != null)
    {
      return $"A schedule with the name '{name}' already exists.";
    }

    var schedule = await repository.CreateAsync(new Schedule
    {
      Name = name,
      CronExpression = cronExpression,
      Prompt = prompt,
      Enabled = true,
    });

    return JsonSerializer.Serialize(new
    {
      schedule.Id,
      schedule.Name,
      schedule.CronExpression,
      schedule.Prompt,
      schedule.Enabled,
      CreatedAt = schedule.CreatedAt.ToString("o"),
    });
  }
}
