using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.muistutin.Data;

namespace toimi.tools.muistutin.Tools;

[McpServerToolType]
public class CompleteReminderTool(ReminderRepository repository)
{
  [McpServerTool, Description("Mark a reminder as completed. For one-time reminders, marks it as done. For recurring reminders, provide occurrenceUtc to mark a specific occurrence as done.")]
  public async Task<string> CompleteReminder(
    [Description("Reminder ID (UUID)")] string id,
    [Description("For recurring reminders: the specific occurrence UTC datetime to mark as done")] string? occurrenceUtc = null)
  {
    if (!Guid.TryParse(id, out var reminderId))
    {
      return "Invalid reminder ID format. Expected a UUID.";
    }

    var reminder = await repository.GetByIdAsync(reminderId);

    if (reminder == null)
    {
      return "Reminder not found.";
    }

    if (!string.IsNullOrEmpty(reminder.RecurrenceRule))
    {
      if (occurrenceUtc == null)
      {
        return "This is a recurring reminder. Provide occurrenceUtc to mark a specific occurrence as done.";
      }

      if (!DateTimeOffset.TryParse(occurrenceUtc, out var parsedOccurrence))
      {
        return "Invalid occurrenceUtc format. Use ISO 8601 (e.g. 2026-03-15T09:00:00Z).";
      }

      await repository.CompleteOccurrenceAsync(reminderId, parsedOccurrence);
      return $"Occurrence {occurrenceUtc} of recurring reminder '{reminder.Title}' marked as done.";
    }

    await repository.CompleteAsync(reminderId);
    return $"Reminder '{reminder.Title}' marked as done.";
  }
}
