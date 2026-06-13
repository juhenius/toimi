namespace toimi.tools.ajastin.Data;

public class Schedule
{
  public Guid Id { get; set; }
  public required string Name { get; set; }
  public string? CronExpression { get; set; }
  public DateTimeOffset? RunAt { get; set; }
  public required string Prompt { get; set; }
  public bool Enabled { get; set; }
  public DateTimeOffset? LastRunAt { get; set; }
  public DateTimeOffset CreatedAt { get; set; }
  public DateTimeOffset UpdatedAt { get; set; }
  public ICollection<ScheduleRun> Runs { get; set; } = [];
}
