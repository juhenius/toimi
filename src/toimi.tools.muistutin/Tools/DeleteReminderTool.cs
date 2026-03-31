using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.muistutin.Data;

namespace toimi.tools.muistutin.Tools;

[McpServerToolType]
public class DeleteReminderTool(ReminderRepository repository)
{
  [McpServerTool, Description("Delete a reminder entirely. For recurring reminders, this deletes the entire series.")]
  public async Task<string> DeleteReminder(
    [Description("Reminder ID (UUID)")] string id)
  {
    if (!Guid.TryParse(id, out var reminderId))
    {
      return "Invalid reminder ID format. Expected a UUID.";
    }

    var deleted = await repository.DeleteAsync(reminderId);

    return deleted
      ? "Reminder deleted."
      : "Reminder not found.";
  }
}
