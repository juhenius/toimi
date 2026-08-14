namespace toimi.tools.ruutu.Data.Entities;

public class DisplayEvent
{
  public long Id { get; set; }
  public int DisplayId { get; set; }
  public required string EventType { get; set; }       // "tap" | "check" | "dismiss" | "overlay_dropped"
  public string? Target { get; set; }
  public string? Value { get; set; }                    // jsonb stored as string
  public string? ForwardOutcome { get; set; }           // "ok" | "error: ..." | null (no action matched)
  public DateTimeOffset CreatedAt { get; set; }

  public Display? Display { get; set; }
}
