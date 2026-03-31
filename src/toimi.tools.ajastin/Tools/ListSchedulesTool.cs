using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.ajastin.Data;

namespace toimi.tools.ajastin.Tools;

[McpServerToolType]
public class ListSchedulesTool(ScheduleRepository repository)
{
  [McpServerTool, Description("List all scheduled tasks.")]
  public async Task<string> ListSchedules()
  {
    var schedules = await repository.GetAllAsync();
    var list = schedules.ToList();

    if (list.Count == 0)
    {
      return "No schedules found.";
    }

    return JsonSerializer.Serialize(list.Select(s => new
    {
      s.Id,
      s.Name,
      s.CronExpression,
      s.Prompt,
      s.Enabled,
      LastRunAt = s.LastRunAt?.ToString("o"),
      CreatedAt = s.CreatedAt.ToString("o"),
    }));
  }
}
