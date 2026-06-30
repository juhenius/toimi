using System.Text.Json;

namespace toimi.tools.tietue.Data;

public class TypeDefinition
{
  // Name is the primary key — define_type upserts by name.
  public required string Name { get; set; }
  public required JsonDocument JsonSchema { get; set; }
  public string? Behaviors { get; set; }
  public string? DefaultTriggers { get; set; }
  public DateTimeOffset CreatedAt { get; set; }
  public DateTimeOffset UpdatedAt { get; set; }
}
