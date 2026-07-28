namespace Toimi.Core.Configuration;

public class OpenAIOptions
{
  public required string ApiKey { get; set; }
  public string Model { get; set; } = "gpt-4o";

  /// <summary>Per-request network timeout for LLM calls.</summary>
  public int NetworkTimeoutSeconds { get; set; } = 100;

  /// <summary>Max transient retries (429/5xx) at the SDK pipeline layer.</summary>
  public int MaxRetries { get; set; } = 3;
}

public class ToimiConfiguration
{
  public required OpenAIOptions OpenAI { get; set; }
  public List<McpServerOptions> McpServers { get; set; } = [];

  /// <summary>Hard wall-clock cap for a headless agent run (MCP connect + LLM turns).</summary>
  public int AgentRunTimeoutSeconds { get; set; } = 300;

  /// <summary>Context-window budget used by ContextManager before summarizing older messages.</summary>
  public int MaxContextTokens { get; set; } = 100_000;

  /// <summary>IANA tz stamped onto recurring triggers that omit their own tz, so wall-clock rules survive DST.</summary>
  public string UserTimeZone { get; set; } = "Europe/Helsinki";

  /// <summary>USD per 1M input tokens, for the admin usage view. Defaults track gpt-4o.</summary>
  public decimal TokenPriceInputPer1M { get; set; } = 2.50m;

  /// <summary>USD per 1M output tokens, for the admin usage view.</summary>
  public decimal TokenPriceOutputPer1M { get; set; } = 10.00m;
}
