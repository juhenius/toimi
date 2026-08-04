namespace toimi.tools.tietue.Scripts;

public class ScriptOptions
{
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Script execution budget, sent to suoritin as timeoutMs. The HTTP client
  /// timeout is this +5s and the handler watchdog this +10s, so the scheduler
  /// tick (which holds the tick lock) is always bounded even if suoritin hangs.
  /// Note: suoritin clamps timeoutMs to 60s, so values above 60 are ineffective.
  /// </summary>
  public int TimeoutSeconds { get; set; } = 20;

  /// <summary>
  /// Budget for applying a script's effects (setField + mcpCalls) after the run.
  /// Effects application also happens under the scheduler tick lock, and each
  /// mcpCall connects to the configured MCP servers, so it must be bounded too.
  /// </summary>
  public int EffectsTimeoutSeconds { get; set; } = 60;
}
