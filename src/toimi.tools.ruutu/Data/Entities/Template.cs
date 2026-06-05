namespace toimi.tools.ruutu.Data.Entities;

public class Template
{
  public int Id { get; set; }
  public required string Name { get; set; }
  public required string Description { get; set; }
  public required string SchemaJson { get; set; }       // JSON Schema
  public string? ModernHtml { get; set; }
  public string? LegacyHtml { get; set; }
  public bool IsSeeded { get; set; }
  public DateTimeOffset CreatedAt { get; set; }
  public DateTimeOffset UpdatedAt { get; set; }
}
