namespace toimi.tools.tietue.Data;

public class UniqueKey
{
  public Guid Id { get; set; }
  public required string Type { get; set; }
  public required string Field { get; set; }
  public required string Value { get; set; }
  public Guid EntityId { get; set; }
}
