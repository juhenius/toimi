using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.muistutin.Data;
using toimi.tools.muistutin.Recurrence;

namespace toimi.tools.muistutin.Tools;

[McpServerToolType]
public class CreateReminderTool(ReminderRepository repository)
{
  [McpServerTool, Description("Create a reminder for a specific time. Supports one-time and recurring reminders using RFC 5545 recurrence rules (e.g. FREQ=WEEKLY;BYDAY=MO, FREQ=MONTHLY;BYMONTHDAY=1, FREQ=YEARLY).")]
  public async Task<string> CreateReminder(
    [Description("Short title for the reminder")] string title,
    [Description("UTC datetime in ISO 8601 format (e.g. 2026-03-15T09:00:00Z)")] string dateTimeUtc,
    [Description("IANA timezone identifier (e.g. Europe/Helsinki)")] string timeZone,
    [Description("Optional longer description")] string? description = null,
    [Description("Optional RFC 5545 recurrence rule (e.g. FREQ=WEEKLY;BYDAY=MO)")] string? recurrenceRule = null)
  {
    if (!DateTimeOffset.TryParse(dateTimeUtc, out var parsedDateTime))
    {
      return "Invalid dateTimeUtc format. Use ISO 8601 (e.g. 2026-03-15T09:00:00Z).";
    }

    var displayEndUtc = RecurrenceExpander.ComputeDisplayEndUtc(parsedDateTime, recurrenceRule);

    var reminder = await repository.CreateAsync(new Reminder
    {
      Title = title,
      Description = description,
      DateTimeUtc = parsedDateTime,
      TimeZone = timeZone,
      RecurrenceRule = recurrenceRule,
      DisplayEndUtc = displayEndUtc,
    });

    return JsonSerializer.Serialize(new
    {
      reminder.Id,
      reminder.Title,
      reminder.Description,
      DateTimeUtc = reminder.DateTimeUtc.ToString("o"),
      reminder.TimeZone,
      reminder.RecurrenceRule,
      CreatedAt = reminder.CreatedAt.ToString("o"),
    });
  }
}
