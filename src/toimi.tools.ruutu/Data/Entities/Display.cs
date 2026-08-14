namespace toimi.tools.ruutu.Data.Entities;

public class Display
{
  public int Id { get; set; }
  public required string Identifier { get; set; }
  public string? Tier { get; set; }                     // "modern" | "legacy" | null
  public bool TierOverride { get; set; }
  public string? LastUserAgent { get; set; }
  public int? ViewportWidth { get; set; }
  public int? ViewportHeight { get; set; }
  public string? Orientation { get; set; }              // "landscape" | "portrait" | null
  public string? CurrentTemplate { get; set; }
  public string? CurrentData { get; set; }              // jsonb stored as string
  public string? CurrentActions { get; set; }           // jsonb: {"<type>[:<target>]": "<webhook url>"}, scene-scoped
  public DateTimeOffset? CurrentPushedAt { get; set; }
  public string OverlayStack { get; set; } = "[]";      // jsonb: array of {template, data, enqueued_at}
  public string? IdleTemplate { get; set; }
  public string? IdleData { get; set; }                 // jsonb stored as string
  public DateTimeOffset? LastSeenAt { get; set; }
  public DateTimeOffset CreatedAt { get; set; }

  public ICollection<DisplayEvent> Events { get; set; } = [];
}
