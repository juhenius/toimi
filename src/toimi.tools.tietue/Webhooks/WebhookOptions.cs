namespace toimi.tools.tietue.Webhooks;

public class WebhookOptions
{
  /// <summary>Global kill switch: off makes everything under /hooks a uniform 404.</summary>
  public bool Enabled { get; set; } = true;

  /// <summary>External base URL callers reach /hooks through (e.g. https://toimi.example); null leaves tool responses without a composed url.</summary>
  public string? PublicBaseUrl { get; set; }

  /// <summary>Default firings-per-minute cap per webhook; a webhook anchor's rateLimit overrides it.</summary>
  public int RateLimitPerMinute { get; set; } = 6;

  /// <summary>
  /// Cap on ALL /hooks requests per minute, enforced before authentication (and before the
  /// trigger lookup hits the database) — the only meter unauthenticated probe floods ever see.
  /// </summary>
  public int GlobalRateLimitPerMinute { get; set; } = 120;

  public int MaxBodyBytes { get; set; } = 65536;

  /// <summary>Parallel dispatch consumers — one slow (agent-run) firing must not head-of-line block unrelated webhooks.</summary>
  public int DispatchConcurrency { get; set; } = 4;

  /// <summary>
  /// How long an accepted firing keeps retrying a Busy claim before being dropped. Must
  /// outlast the longest legitimate scheduler tick, which runs its handlers inline while
  /// holding the tick lock — several agent runs can hold it for many minutes.
  /// </summary>
  public int BusyRetryWindowMinutes { get; set; } = 60;
}
