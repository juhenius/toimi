using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Events;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class CompleteOccurrenceTool(EntityEventStore events)
{
  [McpServerTool, Description("Mark a specific occurrence of an entity's trigger as completed, so it won't fire (or fire again). Provide the occurrence's UTC time (ISO 8601). For a one-shot reminder this is its scheduled time.")]
  public async Task<string> CompleteOccurrence(
      [Description("Entity id (GUID)")] string entityId,
      [Description("Occurrence time, ISO 8601 UTC")] string occurrenceUtc)
  {
    if (!Guid.TryParse(entityId, out var id))
    {
      return "Invalid entityId. Expected a GUID.";
    }

    if (!DateTimeOffset.TryParse(occurrenceUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var occ))
    {
      return "Invalid occurrenceUtc. Use ISO 8601 (e.g. 2026-06-20T09:00:00Z).";
    }

    await events.CompleteAsync(id, occ);
    return $"Occurrence {occurrenceUtc} completed.";
  }
}
