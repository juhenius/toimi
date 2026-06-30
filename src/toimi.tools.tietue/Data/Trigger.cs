namespace toimi.tools.tietue.Data;

public class Trigger
{
  public Guid Id { get; set; }
  public Guid EntityId { get; set; }
  public required string Schedule { get; set; }
  public required string HandlerKind { get; set; }
  public string? HandlerConfig { get; set; }
  public string? Source { get; set; }
  public bool Enabled { get; set; } = true;
  public DateTimeOffset? NextFireAt { get; set; }
  public DateTimeOffset? LastFiredAt { get; set; }
  public DateTimeOffset CreatedAt { get; set; }
  public DateTimeOffset UpdatedAt { get; set; }
}
