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

  /// <summary>Context-window budget used by ContextManager before summarizing older messages.</summary>
  public int MaxContextTokens { get; set; } = 100_000;

  /// <summary>USD per 1M input tokens, for the admin usage view. Defaults track gpt-4o.</summary>
  public decimal TokenPriceInputPer1M { get; set; } = 2.50m;

  /// <summary>USD per 1M output tokens, for the admin usage view.</summary>
  public decimal TokenPriceOutputPer1M { get; set; } = 10.00m;
}
