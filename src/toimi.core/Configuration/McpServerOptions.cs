namespace Toimi.Core.Configuration;

public enum McpTransportType
{
  Http,
  Stdio
}

public class McpServerOptions
{
  public required string Name { get; set; }
  public required McpTransportType Transport { get; set; }

  // Http transport
  public string? Url { get; set; }
  public Dictionary<string, string>? Headers { get; set; }

  // Stdio transport
  public string? Command { get; set; }
  public List<string>? Args { get; set; }
  public Dictionary<string, string>? Env { get; set; }
}
