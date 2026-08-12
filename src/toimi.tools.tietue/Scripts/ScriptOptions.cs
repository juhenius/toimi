namespace toimi.tools.tietue.Scripts;

public class ScriptOptions
{
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Script execution budget in seconds, sent to suoritin as timeoutMs. The
  /// full timeout ladder (HTTP client, watchdog, token TTL) is derived from
  /// this in <see cref="ScriptBudget"/> — the single owner of the arithmetic.
  /// </summary>
  public int TimeoutSeconds { get; set; } = 20;

  /// <summary>
  /// Budget for applying a script's effects (setField + mcpCalls) after the run.
  /// Effects application also happens under the scheduler tick lock, and each
  /// mcpCall connects to the configured MCP servers, so it must be bounded too.
  /// </summary>
  public int EffectsTimeoutSeconds { get; set; } = 60;
}
