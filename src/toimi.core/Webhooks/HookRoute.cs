namespace Toimi.Core.Webhooks;

/// <summary>
/// The capability-URL route prefix (ADR 0001). tietue serves it; ruutu's action
/// forwarder rewrites public URLs under it onto the cluster-internal service
/// (ADR 0002). Shared here because the two pods cannot reference each other.
/// </summary>
public static class HookRoute
{
  public const string Base = "/hooks";
}
