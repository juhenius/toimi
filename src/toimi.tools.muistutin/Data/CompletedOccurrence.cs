namespace toimi.tools.muistutin.Data;

public class CompletedOccurrence
{
  public Guid Id { get; set; }
  public Guid ReminderId { get; set; }
  public DateTimeOffset OccurrenceUtc { get; set; }
  public DateTimeOffset CompletedAt { get; set; }
  public Reminder Reminder { get; set; } = null!;
}
