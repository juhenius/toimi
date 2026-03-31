namespace toimi.tools.ajastin.Data;

public class ScheduleRun
{
  public Guid Id { get; set; }
  public Guid ScheduleId { get; set; }
  public DateTimeOffset StartedAt { get; set; }
  public DateTimeOffset? CompletedAt { get; set; }
  public string? Response { get; set; }
  public string? ToolCallsJson { get; set; }
  public bool Success { get; set; }
  public string? Error { get; set; }
  public Schedule Schedule { get; set; } = null!;
}
