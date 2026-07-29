namespace toimi.tools.tietue.Scripts;

public class ScriptOptions
{
  public bool Enabled { get; set; } = true;

  /// <summary>Wall-clock budget for one script evaluation; see ScriptHandler for why this exists.</summary>
  public int TimeoutSeconds { get; set; } = 5;
}
