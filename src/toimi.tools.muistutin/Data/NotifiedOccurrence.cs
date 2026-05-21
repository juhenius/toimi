namespace toimi.tools.muistutin.Data;

public class NotifiedOccurrence
{
  public Guid Id { get; set; }
  public Guid ReminderId { get; set; }
  public DateTimeOffset OccurrenceUtc { get; set; }
  public DateTimeOffset NotifiedAt { get; set; }
  public Reminder Reminder { get; set; } = null!;
}
