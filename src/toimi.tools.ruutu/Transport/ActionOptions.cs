namespace toimi.tools.ruutu.Transport;

/// <summary>
/// Forwarding config ("Actions" section). When both values are set, a
/// capability URL pointing at the public host's /hooks endpoint is rewritten
/// onto the cluster-internal tietue service before the POST — a display's
/// forward never leaves the cluster or depends on the ingress certificate.
/// Unset (the default) forwards URLs verbatim.
/// </summary>
public class ActionOptions
{
  /// <summary>The public host capability URLs are composed with (tietue's Webhooks__PublicBaseUrl host), e.g. "toimi.example.com".</summary>
  public string? PublicHookHost { get; set; }

  /// <summary>Cluster-internal base to send those forwards to instead, e.g. "http://toimi-tools-tietue.apps.svc.cluster.local".</summary>
  public string? InternalHookBase { get; set; }
}
