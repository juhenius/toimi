namespace toimi.tools.muistutin.Data;

public class Reminder
{
  public Guid Id { get; set; }
  public required string Title { get; set; }
  public string? Description { get; set; }
  public DateTimeOffset DateTimeUtc { get; set; }
  public required string TimeZone { get; set; }
  public string? RecurrenceRule { get; set; }
  public DateTimeOffset? DisplayEndUtc { get; set; }
  public bool IsCompleted { get; set; }
  public DateTimeOffset CreatedAt { get; set; }
  public ICollection<CompletedOccurrence> CompletedOccurrences { get; set; } = [];
}
