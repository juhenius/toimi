using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Cronos;
using ModelContextProtocol.Server;
using toimi.tools.ajastin.Data;

namespace toimi.tools.ajastin.Tools;

[McpServerToolType]
public class CreateScheduleTool(ScheduleRepository repository)
{
  [McpServerTool, Description("Create a scheduled task that runs an AI prompt at a specific time or on a cron schedule. The prompt will be executed as a full agent session with access to all tools. Provide either cronExpression for recurring or runAt for one-time.")]
  public async Task<string> CreateSchedule(
    [Description("Short name for the schedule")] string name,
    [Description("The prompt to execute")] string prompt,
    [Description("Cron expression for recurring (5 fields: minute hour day month weekday, e.g. '0 7 * * *' for daily at 7am UTC)")] string? cronExpression = null,
    [Description("UTC datetime for one-time execution (ISO 8601, e.g. '2026-04-03T15:00:00Z')")] string? runAt = null)
  {
    if (cronExpression is null && runAt is null)
    {
      return "Provide either cronExpression (recurring) or runAt (one-time).";
    }

    if (cronExpression is not null && runAt is not null)
    {
      return "Provide only one of cronExpression or runAt, not both.";
    }

    DateTimeOffset? parsedRunAt = null;
    if (cronExpression is not null)
    {
      try
      {
        CronExpression.Parse(cronExpression);
      }
      catch (CronFormatException ex)
      {
        return $"Invalid cron expression: {ex.Message}";
      }
    }
    else if (runAt is not null)
    {
      if (!DateTimeOffset.TryParse(runAt, CultureInfo.InvariantCulture, out var parsed))
      {
        return "Invalid runAt format. Use ISO 8601 (e.g. 2026-04-03T15:00:00Z).";
      }

      if (parsed <= DateTimeOffset.UtcNow)
      {
        return "runAt must be in the future.";
      }

      parsedRunAt = parsed;
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
      RunAt = parsedRunAt,
      Prompt = prompt,
      Enabled = true,
    });

    return JsonSerializer.Serialize(new
    {
      schedule.Id,
      schedule.Name,
      schedule.CronExpression,
      RunAt = schedule.RunAt?.ToString("o"),
      schedule.Prompt,
      schedule.Enabled,
      CreatedAt = schedule.CreatedAt.ToString("o"),
    });
  }
}
