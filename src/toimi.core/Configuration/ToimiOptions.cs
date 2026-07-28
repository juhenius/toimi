namespace Toimi.Core.Configuration;

public class OpenAIOptions
{
  public required string ApiKey { get; set; }
  public string Model { get; set; } = "gpt-4o";
}

public class ToimiConfiguration
{
  public required OpenAIOptions OpenAI { get; set; }
  public List<McpServerOptions> McpServers { get; set; } = [];

  /// <summary>Hard wall-clock cap for a headless agent run (MCP connect + LLM turns).</summary>
  public int AgentRunTimeoutSeconds { get; set; } = 300;
}
