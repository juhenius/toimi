using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.muistutin.Data;
using toimi.tools.muistutin.Recurrence;

namespace toimi.tools.muistutin.Tools;

[McpServerToolType]
public class ListRemindersTool(ReminderRepository repository)
{
  private sealed record ReminderOccurrence(
    Guid Id, string Title, string? Description, string OccurrenceUtc,
    string TimeZone, bool IsRecurring, string? RecurrenceRule);

  [McpServerTool, Description("List reminders due within a time window. Returns all one-time and expanded recurring reminders in the specified UTC range.")]
  public async Task<string> ListReminders(
    [Description("Start of time window in UTC ISO 8601 format")] string fromUtc,
    [Description("End of time window in UTC ISO 8601 format")] string toUtc)
  {
    if (!DateTimeOffset.TryParse(fromUtc, out var from))
    {
      return "Invalid fromUtc format. Use ISO 8601 (e.g. 2026-03-15T09:00:00Z).";
    }

    if (!DateTimeOffset.TryParse(toUtc, out var to))
    {
      return "Invalid toUtc format. Use ISO 8601 (e.g. 2026-03-15T09:00:00Z).";
    }

    var reminders = await repository.GetByDateRangeAsync(from, to);
    var results = new List<ReminderOccurrence>();

    foreach (var reminder in reminders)
    {
      var occurrences = RecurrenceExpander.ExpandOccurrences(
        reminder.DateTimeUtc, reminder.RecurrenceRule, from, to).ToList();

      var completedOccurrences = (await repository.GetCompletedOccurrencesAsync(reminder.Id, from, to))
          .Select(co => co.OccurrenceUtc)
          .ToHashSet();

      foreach (var occurrence in occurrences)
      {
        if (completedOccurrences.Contains(occurrence))
        {
          continue;
        }

        results.Add(new ReminderOccurrence(
          reminder.Id,
          reminder.Title,
          reminder.Description,
          occurrence.ToString("o"),
          reminder.TimeZone,
          !string.IsNullOrEmpty(reminder.RecurrenceRule),
          reminder.RecurrenceRule));
      }
    }

    results.Sort((a, b) => string.Compare(a.OccurrenceUtc, b.OccurrenceUtc, StringComparison.Ordinal));

    return results.Count == 0
      ? "No reminders in this time window."
      : JsonSerializer.Serialize(results);
  }
}
