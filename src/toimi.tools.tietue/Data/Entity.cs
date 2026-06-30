using System.Text.Json;

namespace toimi.tools.tietue.Data;

public class Entity
{
  public Guid Id { get; set; }
  public required string Type { get; set; }
  public required JsonDocument Data { get; set; }
  public string[] Tags { get; set; } = [];
  public DateTimeOffset CreatedAt { get; set; }
  public DateTimeOffset UpdatedAt { get; set; }
}
