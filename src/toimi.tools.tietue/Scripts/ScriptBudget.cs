namespace toimi.tools.tietue.Scripts;

/// <summary>
/// The script-run timeout ladder, derived once from <see cref="ScriptOptions"/>:
/// Script (the wire timeoutMs) &lt; HttpTimeout (+httpMargin) &lt; Watchdog
/// (+watchdogMargin) &lt; TokenTtl (Watchdog + 20s). Every outer layer outlives
/// the one beneath it, so the scheduler tick (which holds the tick lock while a
/// handler runs) is bounded even if suoritin hangs. Effects is the separate
/// post-run budget for applying setField/mcpCall effects under the same lock.
/// </summary>
public sealed class ScriptBudget
{
  /// <summary>Counterpart: suoritin clamps timeoutMs at 60s (executor.ts MAX_TIMEOUT_MS). Keep equal.</summary>
  public const int MaxScriptSeconds = 60;

  public ScriptBudget(TimeSpan script, TimeSpan httpMargin, TimeSpan watchdogMargin, TimeSpan effects)
  {
    if (script <= TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(script), script, "script budget must be positive");
    }

    if (httpMargin < TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(httpMargin), httpMargin, "HTTP margin must be non-negative");
    }

    if (watchdogMargin < httpMargin)
    {
      throw new ArgumentOutOfRangeException(nameof(watchdogMargin), watchdogMargin, "watchdog margin must not undercut the HTTP margin");
    }

    Script = script;
    HttpTimeout = script + httpMargin;
    Watchdog = script + watchdogMargin;
    TokenTtl = Watchdog + TimeSpan.FromSeconds(20);
    Effects = effects;
  }

  /// <summary>Sandbox execution budget — sent to suoritin as timeoutMs.</summary>
  public TimeSpan Script { get; }

  /// <summary>The named suoritin HttpClient's Timeout (Program.cs).</summary>
  public TimeSpan HttpTimeout { get; }

  /// <summary>ScriptHandler's outer WaitAsync bound on the whole suoritin call.</summary>
  public TimeSpan Watchdog { get; }

  /// <summary>RunTokenStore TTL for the extract() run token — outlives the watchdog.</summary>
  public TimeSpan TokenTtl { get; }

  /// <summary>Post-run budget for ScriptEffectApplier.</summary>
  public TimeSpan Effects { get; }

  public int ScriptMs => (int)Script.TotalMilliseconds;

  public static ScriptBudget From(ScriptOptions options)
  {
    return new ScriptBudget(
      TimeSpan.FromSeconds(Math.Min(options.TimeoutSeconds, MaxScriptSeconds)),
      TimeSpan.FromSeconds(5),
      TimeSpan.FromSeconds(10),
      TimeSpan.FromSeconds(options.EffectsTimeoutSeconds));
  }
}
