namespace Toimi.Core.Configuration;

public class OpenAIOptions
{
  public required string ApiKey { get; set; }

  /// <summary>The fast model — the default tier every agent turn runs on, chosen for cost.</summary>
  public string FastModel { get; set; } = "gpt-4o";

  /// <summary>The smart model for harder work. Optional: when unset, the fast model stands in wherever the smart tier is asked for.</summary>
  public string? SmartModel { get; set; }

  /// <summary>True only when a smart model is configured and differs from the fast model — the single "smart tier really exists" predicate (provider and pricing must agree).</summary>
  public bool HasDistinctSmartModel =>
    !string.IsNullOrWhiteSpace(SmartModel) && SmartModel != FastModel;

  /// <summary>Per-request network timeout for LLM calls.</summary>
  public int NetworkTimeoutSeconds { get; set; } = 100;

  /// <summary>Max transient retries (429/5xx) at the SDK pipeline layer.</summary>
  public int MaxRetries { get; set; } = 3;
}

public class ToimiConfiguration
{
  public required OpenAIOptions OpenAI { get; set; }
  public List<McpServerOptions> McpServers { get; set; } = [];

  /// <summary>Hard wall-clock cap for a headless agent run (MCP connect + LLM turns) and for each delegated subtask.</summary>
  public int AgentRunTimeoutSeconds { get; set; } = 300;

  /// <summary>Context-window budget used by ConversationContext compaction before summarizing older messages.</summary>
  public int MaxContextTokens { get; set; } = 100_000;

  /// <summary>IANA tz stamped onto recurring triggers that omit their own tz, so wall-clock rules survive DST.</summary>
  public string UserTimeZone { get; set; } = "Europe/Helsinki";

  /// <summary>USD per 1M input tokens on the fast tier, for the admin usage view. Defaults track gpt-4o.</summary>
  public decimal FastPriceInputPer1M { get; set; } = 2.50m;

  /// <summary>USD per 1M output tokens on the fast tier, for the admin usage view.</summary>
  public decimal FastPriceOutputPer1M { get; set; } = 10.00m;

  /// <summary>USD per 1M input tokens on the smart tier, for the admin usage view.</summary>
  public decimal SmartPriceInputPer1M { get; set; } = 2.50m;

  /// <summary>USD per 1M output tokens on the smart tier, for the admin usage view.</summary>
  public decimal SmartPriceOutputPer1M { get; set; } = 10.00m;

  /// <summary>
  /// Price pair for a message's attributed model name. Prices are keyed by tier,
  /// so only the smart model's name maps to the smart pair; anything else —
  /// including null attribution on rows from before attribution existed — prices
  /// as fast, the tier every turn starts on.
  /// </summary>
  public (decimal InputPer1M, decimal OutputPer1M) PricesForModel(string? model)
  {
    return OpenAI.HasDistinctSmartModel && model == OpenAI.SmartModel
      ? (SmartPriceInputPer1M, SmartPriceOutputPer1M)
      : (FastPriceInputPer1M, FastPriceOutputPer1M);
  }
}
