namespace toimi.tools.muistio.Data;

public class Memory
{
  public Guid Id { get; set; }
  public required string Content { get; set; }
  public string? Category { get; set; }
  public string[] Tags { get; set; } = [];
  public string Source { get; set; } = "user";
  public bool Confirmed { get; set; } = true;
  public DateTimeOffset? ExpiresAt { get; set; }
  public DateTimeOffset CreatedAt { get; set; }
  public DateTimeOffset UpdatedAt { get; set; }
}
