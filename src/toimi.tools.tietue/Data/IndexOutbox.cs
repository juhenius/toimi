namespace toimi.tools.tietue.Data;

public class IndexOutbox
{
  public Guid Id { get; set; }
  public Guid EntityId { get; set; } // intentionally no FK: delete ops must outlive the entity
  public required string Type { get; set; }
  public required string Op { get; set; } // "upsert" | "delete"
  public int Attempts { get; set; }
  public string? LastError { get; set; }
  public DateTimeOffset? LastAttemptAt { get; set; }
  public DateTimeOffset CreatedAt { get; set; }
}
