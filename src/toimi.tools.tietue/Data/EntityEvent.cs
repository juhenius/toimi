namespace toimi.tools.tietue.Data;

public class EntityEvent
{
  public Guid Id { get; set; }
  public Guid EntityId { get; set; }
  public DateTimeOffset OccurrenceUtc { get; set; }
  public required string Kind { get; set; }
  public required string Status { get; set; }
  public string? Result { get; set; }
  public DateTimeOffset CreatedAt { get; set; }
}
